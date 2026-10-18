# 社区时间点补充（IntroDB + TheIntroDB）

在本地分析之后，为**没找到片头或片尾**的剧集/电影，从社区数据库补充时间点。本地分析始终优先，社区结果不会替换本地或用户片段。

## 版本追踪

| 项目 | 值 |
| --- | --- |
| 基线 | `inori/outro-tail-5s`（上游 `12.0/v12.0.4.0` + 片尾保留补丁） |
| 补丁分支 | `inori/community-fallback` |
| 程序集 / 文件版本 | `12.0.4.0`，保持原插件身份 |
| 新增数据库迁移 | 无（新来源值 `Community = 7` 存入已有整数列） |
| 宿主兼容验证 | Jellyfin `12.1.0`、`12.2.0` 实际宿主程序集 |

## 规则

1. 片头（Introduction）和片尾（Credits）两种模式；回顾、预告、广告不查询。
2. 每季每个模式先跑完上游的本地分析；只有结果为“无片段”的条目才查询社区库。已有本地片段、用户片段、本地分析失败（交给下次重试）的条目都不查询。
3. 来源顺序：IntroDB → TheIntroDB。第一个给出可用时间点的来源生效。
4. 社区时间点经过与本地结果相同的 `TimeAdjustmentHelper`，所以 `IntroEndOffset` 等偏移设置照常生效；写入后的片尾仍经过片尾保留补丁裁剪。
5. 片段来源记为 `Community`，在片段编辑器中显示为 “community”，可像其他自动片段一样删除（墓碑保护不会被再次写入）。

### 版本不一致保护

社区时间点是别人在自己的片源上标的。满足下列任一条件即视为另一个版本，整组丢弃：

- 时间点超出本地文件结尾超过容差（默认 5 秒）；
- TheIntroDB 报告的参考版本时长与本地时长相差超过容差。

容差内超出文件结尾的部分被截到文件结尾。

### 两个库的寻址

| | IntroDB | TheIntroDB |
| --- | --- | --- |
| 接口 | `GET https://api.introdb.app/segments` | `GET https://api.theintrodb.org/v4/media` |
| 剧集 ID | 剧集的 IMDb ID + 季 + 集 | 依次尝试 TVDB、TMDB、IMDb（某个 ID 未映射时服务返回 404，所以逐个尝试） |
| 非默认播出顺序 | 跳过（服务不能换算） | 附带 `mapping=<顺序>` 由服务换算 |
| 特别篇（第 0 季） | 跳过 | 支持 |
| 认证 | 匿名读取 | 匿名读取；每 IP 每天 5000 次，每 10 秒 30 次 |
| 本补丁节流 | 每秒 1 次 | 每 0.4 秒 1 次 |

两个库都禁止批量下载，本补丁只按需逐条查询；同一条目在一次分析里片头、片尾共用一次查询结果。

### 失败处理

- 404、空结果、无法解析：视为“社区库没有”，按正常流程记为已分析。
- 429、5xx、超时、网络错误：本次不写入，条目标为分析失败，下次扫描重试。429 后按服务返回的重置时间暂停该来源。

## 配置与重新分析

设置页新增 “Community” 标签：

| 配置 | 默认 | 说明 |
| --- | --- | --- |
| `EnableCommunityFallback` | 关 | 总开关 |
| `CommunityUseIntroDb` | 开 | 启用 IntroDB |
| `CommunityUseTheIntroDb` | 开 | 启用 TheIntroDB |
| `CommunityDurationToleranceSeconds` | 5 | 版本不一致容差 |

- **默认关闭时，片头/片尾分析 hash 与此前完全一致**（固定测试 `353008E48A5F559E`），升级不触发任何重扫。
- 开启后 hash 变化，但切换时只重新打开**没有片段**的结果：已有片段的条目直接沿用到新 hash（`CommunityHashAdoption`），所以开启只会让“无片段”的条目在下次扫描时查询社区库，不会重算整库。
- 关闭时，来源为 `Community` 的结果重新打开，其余沿用。

## 与上游合并

改动集中在新目录 `IntroSkipper/Community/`、`web/src/tabs/community.ts` 与新测试文件；对上游文件只有小钩子：

| 文件 | 改动 |
| --- | --- |
| `Data/SegmentSource.cs` | 新增 `Community = 7` |
| `Configuration/PluginConfiguration.cs` | 4 个配置项 |
| `Helper/ConfigHasher.cs` | 开启时才追加的 hash 片段，及带开关参数的重载 |
| `Helper/LegacyAnalysisCompatibility.cs` | 旧版本 hash 升级固定到“关闭”状态 |
| `Manager/QueueManager.cs` | 旧 hash 升级后调用 `CommunityHashAdoption` |
| `Manager/AnalyzerTaskFactory.cs` | 注入来源，按次创建 `CommunityFallback` |
| `ScheduledTasks/BaseItemAnalyzerTask.cs` | 本地分析后调用 `FillAsync` |
| `PluginServiceRegistrator.cs` | 注册两个来源 |
| `web/src/{types.ts,main.ts,validation/rules.ts,components/segment-editor.ts}` | 配置类型、标签页、校验、来源标签 |

上游若改动这些钩子附近的代码，冲突通常只需重放对应的一两行。

## 验证

2026-10-19 本地：

- 新增 `TestCommunityFallback` 15 项：两库响应解析、ID 顺序与 `mapping`、版本不一致拒绝、本地优先/只补空条目、片头片尾共用一次查询、来源不可用时保持可重试、关闭时不查询、默认 hash 不变、切换开关只重开无片段结果。
- 定向回归 469/469 通过（含片尾补丁、hash、旧版本兼容、整季重分析、credits pass、章节分析、队列、编辑接口等）。
- 真实 API 抽样（一次性，未入库）：《野生的大魔王出現了！》S01E16（本地 1420.032 秒），两个来源分别得到片头 0–85.083 秒（90.083 − 5），片尾 1330.083–1420.032 秒；经片尾保留补丁后播放终点 1415.032 秒。
- 实际 Jellyfin 12.1.0 与 12.2.0 宿主程序集：新代码 JIT、`Series.DisplayOrder`/提供者 ID 读取、`NamedClient.Default` 均通过；片尾补丁兼容 smoke 两个宿主均通过。

未验证：群晖部署后的实际扫描与 Infuse 播放。
