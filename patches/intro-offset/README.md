# 外源片头终点偏移补丁

本 fork 让**其他提供者**写入的原生播放 `Intro` 响应也遵守本插件的 `IntroStartOffset` / `IntroEndOffset`，同时绝不重复扣减本插件自己已经应用过偏移的片段。

## 版本追踪

| 项目 | 值 |
| --- | --- |
| 上游基线 | `12.0` 分支（包含 `12.0/v12.0.6.0`） |
| 上游提交 | `223a0860535f24744ee164bd3797976b6aa3d685` |
| 补丁分支 | `inori/community-fallback`（含片尾保留、社区补充与本补丁） |
| 程序集 / 文件版本 | `12.0.6.0`，保持原插件身份 |
| 补丁发行标签 | `12.0/v12.0.6.0-inori-introffset.1`（GitHub Actions 构建，未签名，源提交 `d283b6b`） |
| 新增生产文件 | `Filters/PlaybackIntroOffsetFilter.cs`、`Filters/PlaybackIntroOffsets.cs`、`Filters/PlaybackSegmentsAction.cs` |
| 修改生产文件 | `Filters/PlaybackFiltersConvention.cs`（原 `PlaybackOutroTailFilterConvention.cs` 更名并同时挂载两个过滤器）、`Filters/PlaybackOutroTailFilter.cs`、`Manager/IJellyfinSegmentStore.cs`、`Manager/JellyfinSegmentStore.cs`、`PluginServiceRegistrator.cs` |

## 根因

分析阶段写入的片段已经在 `TimeAdjustmentHelper.AdjustIntroTimesAsync` 里扣过偏移：起点加 `IntroStartOffset`、终点减 `IntroEndOffset`。因此本插件数据库里的 `Intro` 行本身就是“最终结果”，播放路径不能再扣一次。

问题出在**同一条 `Intro` 响应里还有别的提供者写的行**。以《地球防衛少年》S01E02 为例：

| 来源 | 行 | 说明 |
| --- | --- | --- |
| Intro Skipper | `5.000–85.007` | Opening 章节 `0.000–90.007` 加 5 秒、减 5 秒，终点在片头内 |
| SkipMe.db | `2.496–92.496` | 固定 90 秒区间，终点比 Opening 章节终点晚 2.5 秒，已进入正文 |

客户端可能采用任意一条。外源行的终点没有经过偏移，于是“OP 的 out 点被调到了正文处”。这与黑帧检测无关：该剧 `Segments.Type=0` 的来源只有 Unknown/Chapter/Chromaprint/Community，没有 `SegmentSource.BlackFrame`。

## 规则

只改**已确认由其他提供者写入**的 `Intro` 行，并对它执行与本地结果相同的偏移：

```text
start = snapped ? start : start + IntroStartOffset      // snapped 见下
end   = end - IntroEndOffset
两者按已知时长裁剪到 [0, RunTimeTicks]
end <= start 时该行只从本次响应移除
```

- `snapped` 指原起点不晚于 `EndSnapThreshold`（默认 2 秒，即原起点贴着片头开头）。此时不加起点偏移，除非 `IncludeIntroStartOffsetWhenSnapping` 开启；这与 `TimeAdjustmentHelper` 对吸附起点的默认处理同向。区别在于本地分析会把吸附起点直接拉到 0，而这里**保留外源行自己的起点**：跳转范围只往后或不变，绝不向更早扩大。
- 偏移为负按 0 处理，负偏移不会用来扩大跳过范围。
- A 站外源行 `2.496–92.496`、`IntroStartOffset=IntroEndOffset=5`、时长 1420 秒 → `7.496–87.496`：跳转终点回到片头内。

### 归属判定

播放响应里的 DTO 不携带提供者。过滤器用一次只读查询 `MediaSegments(SegmentProviderId != 本插件, ItemId, Type=Intro)` 取得属于其他提供者的行 id，只有命中该集合的行才被改写：

- 本插件写入且已被 Jellyfin 持久化的行 → 查询结果里没有 → 原样返回，不会二次扣减。
- 本插件写入但 Jellyfin 尚未持久化的行（镜像关闭或投影未完成）→ 同样查不到 → 原样返回（保守正确）。
- 其他提供者的行 → 被改写。
- 归属查询失败（数据库被锁等）→ 记一条 warning 并**原样返回**：播放响应只是跳转提示，不值得让请求失败。
- 只对 Jellyfin `MediaSegments` 表内存在的行生效。若某个提供者的行只以即时结果出现在响应里、从未落库，就无法归属，保持原样——宁可少改，也不猜。

## 精确作用范围

- 仅 `Jellyfin.Api.Controllers.MediaSegmentsController.GetItemSegments` 的成功原生播放 `GET /MediaSegments/{itemId}`；两者与片尾保留补丁共用 `PlaybackSegmentsAction` 的同一匹配定义（Guid `itemId` 参数 + GET）。
- 仅 `Intro` 类型、仅属于其他提供者的行。
- 被改 DTO 与结果包装都创建副本；保留 id、item、类型、排序、分页起点；移除无效行时相应扣减总数。
- `Outro`、`Recap`、`Preview`、`Commercial`、编辑接口、分析器、配置 hash、插件数据库、镜像与投影均不变。
- 两个偏移都为 0（上游默认）时整条路径在读取配置后直接透传，不产生查询。启用偏移且响应含 `Intro` 时，每次播放片段请求多一次按 `ItemId` 过滤的只读查询。
- 不新增配置项：沿用 Detection 页已有的 `IntroStartOffset`、`IntroEndOffset`、`IncludeIntroStartOffsetWhenSnapping`、`EndSnapThreshold`。
- 不清理、不禁用、不隐藏其他提供者的行；如果管理员希望彻底只留一条 `Intro`，仍需自行处理该提供者。

## 验证范围

2026-11-07 本地与 GitHub CI：

- `dotnet build`（`-p:SkipWebBuild=true`）零警告通过（`TreatWarningsAsErrors` + StyleCop）。
- 播放/存储定向用例 110 项通过，覆盖：真实 Bokurano 数值 `2.496–92.496 → 7.496–87.496`、吸附起点两种开关、时长裁剪、未知时长、负偏移、整行被扣空后的移除、分页元数据、原 DTO 不被修改、`JsonResult` 元数据保留、归属查询抛错时透传、非 GET/非 200/非原生控制器/缺路由 id 时零查询、与片尾保留及首集过滤器在同一注册管线内的组合、真实 `JellyfinDbContext` 上 `GetForeignSegmentIdsAsync` 的提供者/条目/类型过滤。
- 全量 `dotnet test` 1558 通过、8 失败，失败全部集中在 `TestAudioFingerprinting`、`TestFFmpegService`、`TestCreditsRunner`，原因是本机 Homebrew `ffmpeg 8.0.1` 无 `chromaprint` filter（`ffmpeg -filters | grep chromaprint` 为 0），与本补丁路径无关。
- GitHub Actions `Fork Release` 14 步全部通过：恢复、`jellyfin-ffmpeg 8.1.3` 安装、**完整测试套件**、Release 构建、打包、发布、上传。Linux CI 的 ffmpeg 带 chromaprint，本机缺失导致的 8 项失败在 CI 未出现。
- 发行运行 `42386559583`（提交 `d283b6b`）：资产已回下载，`shasum -a 256 -c SHA256SUMS.txt` 两项均 OK；DLL 大小 1172992 字节，SHA256 `32778ff239b6c7a45e905a61065f0843471763964e9a0ecdae75d0e22f5139fa`，内部 `CommitHash` 为 `d283b6b0f3c1a2efa966434203406c989dab187f`。
- 未部署、未重启、未改服务器配置；真实客户端跳转与 API 输出仍待验收。

## 复测

```bash
dotnet test IntroSkipper.Tests/IntroSkipper.Tests.csproj -p:SkipWebBuild=true \
  --filter "FullyQualifiedName~TestPlaybackIntroOffsetFilter|FullyQualifiedName~TestPlaybackFiltersConvention|FullyQualifiedName~TestPlaybackOutroTail|FullyQualifiedName~TestJellyfinSegmentStore"
```

重新发行：`gh workflow run "Fork Release" --repo InoriHimea/intro-skipper --ref inori/outro-tail-5s -f tag='12.0/v12.0.6.0-inori-introffset.N'`。该 workflow 不改版本号、不签名、不派发 manifest、不在上游发布线上打标签。

不连接或修改任何运行中的 Jellyfin 服务。
