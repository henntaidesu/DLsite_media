# R-18MediaLibrary

一个用于**获取、管理、浏览与播放成人向作品**的 Windows 桌面工具（WPF / .NET 10 / C#）。

程序集成三个来源，并内置媒体库、下载队列、内嵌播放器（视频 + 音频）以及可选的局域网 Web 访问服务：

- **Anime-sharing 论坛**（[https://www.anime-sharing.com/](https://www.anime-sharing.com/)）——检索论坛贴子中的 DLsite 作品（RJ 号）网盘链接，**通过 Debrid-Link 网盘中转站解析并下载**。
- **asmr.one 官方 API**——针对语音 / ASMR（SOU）作品的直链下载，保留原始目录结构。
- **Pixiv FANBOX（经 pawchive 存档站）**——按作家检索 FANBOX 投稿并直链下载，免登录。

> 界面、日志、注释均为中文。本项目于 2026 年 6 月由旧的 Python/PyQt 版本完整重写为 C#；运行数据（`R-18MediaLibrary.db`、`images/`、`log/`）保存在仓库根目录。
>
> 项目原名 DLsiteMedia。老版本的 `DLsiteMedia.db`（以及更早的 `DASD.db`）放在同一目录下即可，启动时会自动改名迁移，数据不丢。

---

## ⚠️ 关于 Anime-sharing 下载：必须使用 Debrid-Link 中转站

从 **Anime-sharing 论坛**下载作品时，贴子里的链接指向 Rapidgator、Katfile 等网盘。这些网盘对普通用户有速度 / 并发 / 直连限制，因此本程序**不直接下载网盘链接**，而是先经由 **[Debrid-Link](https://debrid-link.com/) 网盘中转站**把链接解析成可高速直连的地址后再下载。

使用前请准备：

1. 注册 Debrid-Link 账号并开通会员（付费中转服务）。
2. 在 **系统设置 → 下载** 中填入 **Debrid-Link API Key**（对应配置 `debrid.api_key`）。
3. 若网络需要，配置代理（`proxy` 段，几乎所有 HTTP 请求都会使用它）。

未配置 Debrid-Link API Key 时，Anime-sharing 来源无法解析下载链接。

> **asmr.one** 与 **FANBOX** 两个来源都是直链下载，**不需要** Debrid-Link。asmr.one 需要在设置中配置账号；FANBOX 免登录、无需任何账号。

---

## 功能速览

- **三来源检索下载**：「作品搜索」页顶部的来源下拉切换 `DLsite` / `FANBOX`。
  - DLsite：输入 RJ 号即可检索；语音 / ASMR（SOU）作品可优先走 asmr.one 直链（可在设置中按作品类型选择优先来源，asmr.one 没有该作品时自动回退论坛），其余作品走 Anime-sharing + Debrid-Link。
  - FANBOX：输入作家名检索 → 作家主页投稿网格 → 勾选投稿批量下载，或单篇直接下载。
- **下载队列**：多线程下载，支持断点续传、暂停、低速重试、全局限速，429/403 等限流会自动冷却重试；asmr / FANBOX 作品保留原始多级目录树展示。
- **自动化**：可选自动下载 / 自动解压。解压支持 Bandizip 或内置 SharpCompress，自动修复 Shift_JIS 文件名乱码并整理目录；加密压缩包会依次尝试设置里的密码本。
- **媒体库**：按 媒体库 → 社团 → 作品 → 详情 浏览，自动抓取 DLsite 页面元数据、标签、封面与简介。FANBOX 投稿同样入库（作品号形如 `FB12508755`，社团即作家），元数据在下载时就从 pawchive 取好，不再回查 DLsite。
- **多维浏览**：作品标签、作品社团、作品形式、我的收藏。
- **内嵌播放**：视频用 LibVLC（不依赖系统解码器，H.264/H.265 直接播放）；音频用 NAudio 并做响度归一化（目标约 -18 dBFS），支持文件夹播放列表与睡眠定时。
- **局域网 Web 访问**（可选）：内置 HTTP 服务，把媒体库以响应式网页形式暴露给同一局域网内的手机 / 浏览器，可设访问密码。
- **封面图床加速**（可选）：媒体库放在机械硬盘时，翻一页作品卡就是几十次随机读；接入自建图床后封面改由图床提供，桌面端与 Web 端共用同一套缩略图缓存。

---

## FANBOX 来源说明

FANBOX 投稿来自 **pawchive**（Kemono 形态的公开存档站，默认域名 `pawchive.pw`，可在设置中改）。该来源**只做检索与下载**，不提供独立的媒体库浏览视图——下载完的投稿和 DLsite 作品一样落在媒体库目录里，由媒体库统一浏览。

- 检索：站点没有服务端搜索接口，程序会把整站作家表缓存到 `cache/pawchive-creators.json`（24 小时）后在内存里匹配；只命中一个作家时直接进入作家主页。
- 作品目录：`<媒体库>/FB<投稿ID>`，与 DLsite 的 `<媒体库>/<RJ号>` 平级。目录按投稿 ID 命名而非标题——标题会改、会重名。
- 文件命名：图片按站内顺序重编号为 `001.png`、`002.png`（原名是无意义的哈希）；压缩包 / 视频 / PSD 等附件保留原名，形如 `003_原文件名.zip`。
- 附件里的压缩包在开启「自动解压」时会各自解压到以自身命名的子目录；解压失败不会阻塞入库，压缩包原样留在作品目录里。

---

## 界面预览

### 媒体库
浏览已入库（已品悦）的作品，卡片展示封面、作品号与作品形式标记，可搜索、排序、切换显示社团。

![媒体库](img/1.png)

### 作品搜索
输入 RJ 号 / 作品名 / 社团检索，卡片显示 Anime-sharing 命中状态（如 `AS·N帖` / `AS·无`）与扫描状态，可一键组合下载或开启自动下载；切换来源下拉即可改用 FANBOX 按作家检索。

![作品搜索](img/2.png)

### 作品标签
按 DLsite 标签聚合浏览全部作品。

![作品标签](img/3.png)

### 作品形式
按作品形式（ボイス・ASMR、マンガ、CG、FANBOX 等）聚合浏览。

![作品形式](img/4.png)

---

## 构建与运行

需要 .NET 10 SDK。**在仓库根目录**运行（SQLite 数据库 `R-18MediaLibrary.db` 与 `log/`、`images/` 均按当前工作目录定位）：

```bash
# 构建
dotnet build src/R-18MediaLibrary.csproj

# 运行
dotnet run --project src/R-18MediaLibrary.csproj
```

> 工程文件叫 `R-18MediaLibrary.csproj`、产出 `R-18MediaLibrary.exe`，但代码里的命名空间是 `R18MediaLibrary`——C# 命名空间不允许连字符，故 `AssemblyName` 与 `RootNamespace` 在 csproj 里分开设置。

### 单文件发布

在仓库根目录运行 `publish.bat`，生成依赖框架（不打包 .NET 运行时）的单文件 `publish/R-18MediaLibrary.exe`：

```bash
dotnet publish src/R-18MediaLibrary.csproj -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

> LibVLC 的原生引擎无法被打进单文件，需要在 exe 旁保留一个几 MB 的 `libvlc\win-x64\` 目录（hrtfs + lua 数据），它在运行时按文件路径加载。

---

## 配置

所有配置保存在本地 SQLite 数据库 `R-18MediaLibrary.db` 的 `conf` 表中，由 `AppConfig`（[src/Core/AppConfig.cs](src/Core/AppConfig.cs)）管理，可在程序内 **系统设置** 页修改（失焦 / 选择即保存，无独立保存按钮）。

主要设置项：

| 配置 | 说明 |
| --- | --- |
| `proxy` | 代理设置，应用于几乎所有 HTTP 请求 |
| `debrid.api_key` | **Debrid-Link API Key（Anime-sharing 下载必需）** |
| `down_list` | 下载选项：自动下载 / 自动解压 / 线程数 / 命名规则 / 最低速度 / 限速 |
| `downpath` | 下载缓存目录 |
| `unzip.passwords` | 解压密码本，一行一个，加密压缩包按顺序逐个试 |
| `media_lib.libs` | 媒体库路径（JSON） |
| `language.lang` | 界面语言（zh_CN / zh_TW / ja / en） |
| `web_server` | 局域网 Web 访问：开关 / 端口 / 密码 |
| `asmr` | asmr.one 账号：用户名 / 密码 / token / 镜像站点 |
| `asmr_filetype` | asmr.one 按扩展名的文件类型过滤（mp3/mp4/flac/wav/jpg/png/pdf/txt/vtt/lrc） |
| `search.sou_source` | SOU（语音/ASMR）作品的优先来源：`asmr` 或 `anime-sharing` |
| `pawchive.host` | FANBOX 来源使用的 pawchive 站点域名（默认 `pawchive.pw`） |
| `image_host` | 封面图床：开关 / 地址 / 项目 / 令牌 |

---

## 技术栈

- **框架**：WPF / .NET 10 / C#（`UseWindowsForms` 开启以承载 LibVLC 的 `VideoView`）
- **数据库**：`Microsoft.Data.Sqlite`（WAL 模式，参数化 SQL）
- **抓取**：`HtmlAgilityPack`
- **解压**：`SharpCompress`（备用；优先调用已安装的 Bandizip `bz.exe`）
- **视频播放**：`LibVLCSharp` + `LibVLCSharp.WPF` + `VideoLAN.LibVLC.Windows`
- **音频播放**：`NAudio`（响度归一化）
- **MVVM**：`CommunityToolkit.Mvvm`

---

## 免责声明

本工具仅用于个人对已合法获取内容的下载管理与本地播放。请遵守相关网站的服务条款及所在地区法律法规，自行承担使用风险。
