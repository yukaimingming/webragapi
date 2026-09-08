using System.ClientModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using OpenAI;
using Qdrant.Client;
using Scalar.AspNetCore;
using Serilog;
using WebRagApi.Models;
using WebRagApi.Services;
using WebRagApi.Services.Ingestion;
using WebRagApi.Services.Retrieval;

var builder = WebApplication.CreateBuilder(args);

// Serilog 同时保留控制台输出，并将所有 ILogger<T> 日志按天写入应用目录下的 logs 文件夹。
// 文件日志包含异常堆栈，便于定位模型接口、向量库和文档解析等依赖异常。
var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDirectory);
builder.Host.UseSerilog((_, loggerConfiguration) => loggerConfiguration
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Qdrant.Client", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logDirectory, "webragapi-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true,
        encoding: System.Text.Encoding.UTF8,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}"));

// ---------- 控制器与 API 文档 ----------
// 枚举（任务/文件状态等）序列化为字符串，接口返回更易读
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
// 内置 OpenAPI 文档生成（.NET 10 自带，接口文档由 Scalar 展示）
builder.Services.AddOpenApi();

// 允许跨域（网页 demo 与接口同源部署，此配置仅为方便外部调试工具调用）
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// 全局异常处理：未捕获异常统一兜底为 ProblemDetails，并记录日志（不再向调用方暴露堆栈）
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// ---------- 聊天模型：任意 OpenAI 兼容接口（配置驱动，生产换模型只改 appsettings.json） ----------
// 当前配置为商汤 SenseNova（开发环境）；生产可无缝换成 DeepSeek / Qwen / 本地 Ollama / vLLM 等，
// 它们都遵循 OpenAI 兼容协议，改 Chat:Endpoint / Chat:ApiKey / Chat:Model 三项即可（见 README"切换模型"）。
var chatEndpoint = builder.Configuration["Chat:Endpoint"] ?? "https://token.sensenova.cn/v1";
var chatApiKey = builder.Configuration["Chat:ApiKey"];
if (string.IsNullOrWhiteSpace(chatApiKey))
{
    // 开发环境：dotnet user-secrets set "Chat:ApiKey" "你的Key"；
    // 生产环境：用环境变量 Chat__ApiKey 或 appsettings.Production.json 提供
    throw new InvalidOperationException(
        "缺少聊天模型 API Key。开发环境执行：dotnet user-secrets set \"Chat:ApiKey\" \"你的Key\"；" +
        "生产环境用环境变量 Chat__ApiKey 或 appsettings.Production.json 配置。");
}
var chatModelName = builder.Configuration["Chat:Model"] ?? "sensenova-6.8-flash-lite";

// 商汤等部分第三方接口会返回空的 finish_reason / tool_call type，OpenAI SDK 解析会抛异常，
// 需在 HTTP 传输层改写（FinishReasonRewriteHandler）。标准服务可设 Chat:RewriteFinishReason=false 关掉（开着也无害）。
var rewriteFinishReason = builder.Configuration.GetValue("Chat:RewriteFinishReason", true);
HttpClient chatHttpClient = rewriteFinishReason
    ? new HttpClient(new FinishReasonRewriteHandler { InnerHandler = new HttpClientHandler() }, disposeHandler: true)
    : new HttpClient();
chatHttpClient.Timeout = Timeout.InfiniteTimeSpan; // 流式响应不能设置整体超时，否则长回复会被掐断

var chatClientOptions = new OpenAIClientOptions
{
    Endpoint = new Uri(chatEndpoint),
    Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(chatHttpClient)
};
var chatOpenAIClient = new OpenAIClient(new ApiKeyCredential(chatApiKey), chatClientOptions);

// 注册聊天客户端（RAG 问答生成，保留 OpenAI 兼容客户端）
builder.Services.AddChatClient(chatOpenAIClient.GetChatClient(chatModelName).AsIChatClient());

// 病历助手 / RAG 生成统一走商汤 HTTP 客户端（可传 reasoning_effort / thinking）
builder.Services.AddHttpClient("SenseNova", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    HttpMessageHandler inner = new HttpClientHandler();
    return rewriteFinishReason
        ? new FinishReasonRewriteHandler { InnerHandler = inner }
        : inner;
});
builder.Services.AddSingleton<SenseNovaCompletionService>();

// ---------- 向量模型：本地 Ollama 的 bge-m3（配置复用 AIChatApp 项目） ----------
var ollamaEndpoint = builder.Configuration["Ollama:Endpoint"] ?? "http://localhost:11434/v1";
var embeddingModelName = builder.Configuration["Ollama:EmbeddingModel"] ?? "bge-m3:567m";
var embeddingBatchSize = builder.Configuration.GetValue("Ollama:BatchSize", 16);

// Ollama 收到大批量请求时 runner 会崩溃，必须包一层 BatchingEmbeddingGenerator 拆小批
var ollamaClient = new OpenAIClient(new ApiKeyCredential("ollama"), new OpenAIClientOptions
{
    Endpoint = new Uri(ollamaEndpoint)
});
var embeddingGenerator = new BatchingEmbeddingGenerator(
    ollamaClient.GetEmbeddingClient(embeddingModelName).AsIEmbeddingGenerator(),
    batchSize: embeddingBatchSize);
builder.Services.AddEmbeddingGenerator(embeddingGenerator);

// ---------- 向量库：Qdrant（Docker 运行，数据挂载在项目本地 qdrant_storage 目录） ----------
var qdrantHost = builder.Configuration["Qdrant:Host"] ?? "localhost";
var qdrantPort = builder.Configuration.GetValue("Qdrant:Port", 6334);
builder.Services.AddSingleton(new QdrantClient(qdrantHost, qdrantPort, https: false));

// ---------- RAG 核心服务 ----------
// 问答策略配置（强类型 Options 模式）
builder.Services.Configure<AiChatOptions>(builder.Configuration.GetSection(AiChatOptions.SectionName));
// 客户端自动更新清单；版本、下载地址和开关由 Update 配置节控制。
builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection(UpdateOptions.SectionName));
// 混合检索：内存 BM25 + RRF 融合 + Cross-Encoder 形态重排（默认问答/检索走 rerank）
builder.Services.Configure<RetrievalOptions>(builder.Configuration.GetSection(RetrievalOptions.SectionName));
builder.Services.AddSingleton<Bm25Index>();
builder.Services.AddSingleton<CrossEncoderReranker>();
// 1. 数据导入器：扫描上传目录，解析文档，生成向量并入库
builder.Services.AddSingleton<DataIngestor>();
//2. 导入任务管理器：后台执行导入任务，提供任务状态查询
builder.Services.AddSingleton<IngestionTaskManager>();
//3. 知识库服务：提供向量检索、问答等功能
builder.Services.AddSingleton<KnowledgeService>();
//4. 语义搜索服务：向量检索 + 聊天模型生成答案
builder.Services.AddSingleton<SemanticSearch>();
//5. 聊天服务：RAG 问答接口
builder.Services.AddSingleton<ChatService>();

var app = builder.Build();

// ---------- 中间件 ----------
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); // /openapi/v1.json
}

// Scalar 接口文档：访问 /scalar 打开可视化文档页面
app.MapScalarApiReference(options =>
{
    options.WithTitle("WebRag API - RAG 知识库问答服务")
        .WithSidebar(true);
});
// 全局异常中间件要放在管道最前面，才能兜住后续所有中间件与控制器的异常
app.UseExceptionHandler();
app.UseCors();
app.UseDefaultFiles(); // wwwroot/index.html 作为首页（网页验证 demo）
app.UseStaticFiles();
app.MapControllers();

// 启动横幅：同时写入控制台和 Serilog 本地文件，便于确认 AI 接口服务已正常运行。
Log.Information("服务启动：Services Running...");
Log.Information(
    "运行环境：AI接口服务正在运行!!! 请勿随意关闭接口服务，避免造成数据丢失!!! Powered by {FrameworkDescription} 强力驱动 on Kestrel",
    RuntimeInformation.FrameworkDescription);

// ---------- 启动时扫描上传目录，自动导入尚未入库的新文档 ----------
// 已存在的文档会被自动过滤，因此服务重启不会重复导入；
// 服务运行期间上传的文档走上传接口的后台任务，导入完成即可被检索（无需重启）
using (var scope = app.Services.CreateScope())
{
    var knowledgeService = scope.ServiceProvider.GetRequiredService<KnowledgeService>();
    var startupTask = knowledgeService.StartStartupIngestion();
    if (startupTask.TotalFiles > 0)
        Console.WriteLine($"启动扫描：发现 {startupTask.TotalFiles} 个文档待导入，任务 ID：{startupTask.Id}（导入在后台进行，不影响服务启动）");
    else
        Console.WriteLine("启动扫描：上传目录中没有待导入的新文档。");
}

Console.WriteLine("接口文档（Scalar）：/scalar");

app.Run();
