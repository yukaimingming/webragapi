# WebRagApi — RAG 知识库问答服务

基于 .NET 10 的 RAG（检索增强生成）知识库 WebAPI。框架参考 ChatRAG（去除了 Aspire），
模型配置复用 AIChatApp（商汤 SenseNova 聊天模型 + 本地 Ollama bge-m3 向量模型），
向量库使用 Qdrant（Docker 运行，数据落本地磁盘）。

## 环境要求

| 组件 | 说明 |
| ---- | ---- |
| .NET 10 SDK | `net10.0` |
| Docker Desktop | 运行 Qdrant（见下方启动命令） |
| Ollama | 需已拉取 `bge-m3:567m` 向量模型（1024 维） |
| 商汤 SenseNova API Key | 配置在 `appsettings.json` 的 `SenseNova:Key` |

### 启动 Qdrant

```bash
docker run -d --name webrag-qdrant -p 6333:6333 -p 6334:6334 \
  -v E:\sourcecode\csharp\webragapi\qdrant_storage:/qdrant/storage qdrant/qdrant
```

数据挂载在项目本地的 `qdrant_storage` 目录，容器删除后数据仍在。

### 启动服务

```bash
dotnet run
```

- 服务地址：<http://localhost:5229>
- 接口文档（Scalar）：<http://localhost:5229/scalar>
- 网页验证 Demo：<http://localhost:5229/>（wwwroot/index.html）

## 接口一览

| 接口 | 方法 | 作用 |
| ---- | ---- | ---- |
| `/api/chat` | POST | AI 问答 + 返回引用来源 |
| `/api/knowledge/documents` | POST | 上传文档（pdf/doc/docx/md，多文件） |
| `/api/knowledge/documents` | GET | 获取知识库文档列表 |
| `/api/knowledge/documents/{id}` | GET | 查看文档详情（含切块预览） |
| `/api/knowledge/documents/{id}` | DELETE | 删除文档及其向量 |
| `/api/knowledge/tasks/{id}` | GET | 查询文档导入进度 |
| `/api/knowledge/search` | POST | 直接测试向量检索 |

## 行为说明

- **问答策略（智慧病历模式）**：检索到的切块始终交给大模型，由模型判断语义相关性后二选一——
  - 知识库内容与问题真正相关 → 基于知识库回答，返回引用来源（响应 `mode = "knowledge_base"`）；
  - 不相关（注意：领域相同 ≠ 相关，模型判断比相似度阈值可靠）→ 医疗健康问题由大模型基于自身医学知识直接推理（`mode = "model"`），非医疗问题礼貌拒答。
  - 请求参数 `allowModelAnswer: false` 可退回严格 RAG（只答知识库内容）。
- **增量导入**：上传与启动扫描都只导入知识库中没有的新文档，同名文档自动过滤（按 Qdrant 中 `documentid` 精确匹配）。
- **无需重启**：上传后后台异步导入（返回 taskId 可查进度），导入完成后立即可被检索/问答，不用重启服务。
- **导入任务面板**：`GET /api/knowledge/tasks` 列出最近任务；0 切块的文档（如扫描件 PDF，暂不支持 OCR）会在任务明细中给出警告。
- **文档目录**：`App_Data/Documents`（相对项目根目录，可在 `appsettings.json` 的 `Knowledge:DocumentsPath` 修改）。
- **切块策略**：语义切块（SemanticSimilarityChunker），每块 ≤1024 token、重叠 50 token。
- **模型**：聊天 `sensenova-6.8-flash-lite`（商汤）；向量 `bge-m3:567m`（本地 Ollama，1024 维，余弦相似度）。

## 切换聊天模型（生产部署）

聊天模型走 **OpenAI 兼容协议**，DeepSeek、Qwen、本地 Ollama、vLLM 等全部兼容，
生产环境换模型**不需要改任何代码**，只改 `appsettings.json` 的 `Chat` 节：

| 部署方式 | Endpoint | ApiKey | Model |
| ---- | ---- | ---- | ---- |
| 商汤 SenseNova（当前开发配置） | `https://token.sensenova.cn/v1` | 商汤 Key | `sensenova-6.8-flash-lite` |
| DeepSeek 官方 API | `https://api.deepseek.com/v1` | DeepSeek Key | `deepseek-chat` / `deepseek-reasoner` |
| 本地 Ollama 跑 DeepSeek 14B | `http://服务器IP:11434/v1` | 任意非空串（如 `ollama`） | `deepseek-r1:14b` |
| 本地 Ollama 跑 Qwen | `http://服务器IP:11434/v1` | 任意非空串 | `qwen2.5:14b` |
| vLLM / Xinference 私有化部署 | `http://服务器IP:8000/v1` | 部署时设置的 Key | 部署时的模型名 |
| 阿里云百炼 DashScope | `https://dashscope.aliyuncs.com/compatible-mode/v1` | DashScope Key | `qwen-plus` 等 |

`Chat:RewriteFinishReason` 保持 `true` 即可（对标准服务是纯透传，只在响应带空 `finish_reason` 时才改写）。

注意：向量模型（Ollama bge-m3）与聊天模型相互独立；**已入库的向量与聊天模型无关**，换聊天模型不需要重新导入文档。
另外，`[知识库]`/`[模型]` 模式标记依赖模型遵循提示词格式的能力，主流 14B 以上模型都没问题；
若换用更小的模型发现 mode 判定不准，接口会自动按相似度兜底判定。

## 技术要点

- 导入管道：`Microsoft.Extensions.DataIngestion`（解析 → 语义切块），
  写入器为自定义 `QdrantChunkWriter`（绕开 SK Qdrant 连接器仅支持 Guid/ulong 键的限制，
  同时把文件元数据直接补写进 Qdrant payload，免维护额外元数据库）。
- .doc（97-2003 二进制格式）解析：自研 `BinaryDocReader`（OpenMcdf 读 OLE 流 + 解析 piece table），
  不依赖本机 Office。
- 商汤接口返回空 `finish_reason` 的问题由 `FinishReasonRewriteHandler` 在 HTTP 层改写（复用 AIChatApp）。
- Ollama 大批量 embedding 会压垮 runner，`BatchingEmbeddingGenerator` 自动拆小批（复用 AIChatApp）。
