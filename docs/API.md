# 城通网盘接口笔记（2026-10 实测）

本文记录本项目逆向出来的城通网盘（ctfile）接口行为。所有结论均为实测，不是猜测。

## 域名分工

| 域名 | 用途 | 备注 |
|---|---|---|
| `url91.ctfile.com` 等 `urlXX.ctfile.com` | 分享页（服务端渲染的模板壳） | 只返回模板，数据靠 JS 拉 |
| `545c.com` | 同一个分享页系统的另一个域名 | |
| `webapi.ctfile.com` | 分享页 / 匿名接口 | `getfile.php`、`get_down_url.php` |
| `api.ctfile.com` | 新版 Web API（`/v4/*`） | 登录、账号相关 |
| `rest.ctfile.com` | 旧版客户端 API（`/p2/*`） | 仍可用，需 token |
| `89-cucc-data.*` / `201-usw-data.*` | 文件 CDN 节点 | 与文件绑定，不可切换 |

分享页真正的下载逻辑在 `https://webstatic.ctfile.com/assets/js/other.js` 与 `otherdownload.js` 里，
本项目就是照它的调用流程实现的。

## 匿名解析链路（不需要账号）

### 1. 取文件信息

```
GET https://webapi.ctfile.com/getfile.php
    ?path=f
    &f=<分享码，形如 37476991-1447877437-0eb71b>
    &passcode=<提取码>
    &r=<随机数>
    &ref=
    &url=<分享页完整地址，需 URL 编码>
```

需要带 `Referer: <分享页地址>` 和 `X-Requested-With: XMLHttpRequest`。

关键返回字段：

```json
{
  "code": 200,
  "file": {
    "file_name": "xxx.txt",
    "file_size": "13.50 MB",
    "userid": 37476991,
    "file_id": 1447877437,
    "file_chk": "a40520728d7a6fe87b6bf0b3396f1bee",
    "start_time": 1791187176,
    "wait_seconds": 0,
    "xtredirect": "xtc37476991-f1447877437-e82eb6-txtxiaoshuo",
    "free_speed": "4 分钟",
    "software_speed": "4 分钟",
    "vip_speed": "2 秒"
  }
}
```

错误码：
- `423` 需要提取码
- `403` 分享失效 / 被限制
- `404` 分享不存在

注意 `wait_seconds` 返回 0，所以 API 层面**没有**网页上那个「4 分钟等待」，可以直接取链。

### 2. 换真实下载地址

```
GET https://webapi.ctfile.com/get_down_url.php
    ?uid=<userid>&fid=<file_id>&file_chk=<file_chk>
    &start_time=<start_time>&wait_seconds=<wait_seconds>&rd=<随机数>
```

返回 `{"downurl": "https://...", "file_size": 14160254}`。

直链有效期约 6 小时（看 `ctt` 参数）。

## 账号链路（客户端接口，能拿 2 线程）

### 1. 登录

```
POST https://api.ctfile.com/v4/user/auth/login
Content-Type: application/json
Origin: https://my.ctfile.com

{"email": "xxx@example.com", "password": "xxx"}
```

> 字段名是 `email`，不是 `username`。用 `username` 会返回 `api.auth.missing_credentials`。

返回 `data.token`（64 位）、`data.refresh_token`、`data.expires_in`（**只有 900 秒 / 15 分钟**，所以每次都要重新登录）。

错误：
- `401 invalid_credentials` 账号密码错
- `401 multi_login_locked` 账号被风控锁定（多用户登录）

### 2. 用 xtlink 列文件

```
POST https://rest.ctfile.com/p2/browser/file/list
{"xtlink": "ctfile://<xtredirect>", "token": "<token>", "reload": false}
```

**`xtlink` 必须是 `"ctfile://" + getfile.php 返回的 xtredirect`**，
直接把 `uid-fid-chk` 分享码当 xtlink 会报「找不到小通链接」。

返回 `results[]`，每项有 `key`（文件 id）、`name`、`size`、`icon`（`folder` 表示文件夹）。

### 3. 取下载地址

```
POST https://rest.ctfile.com/p2/browser/file/fetch_url
{"xtlink": "...", "file_id": "<上一步的 key>", "token": "..."}
```

返回 `download_url`，参数里 `limit=2`（比匿名的 `limit=1` 多一倍并发）。

**实验证明 `fetch_url` 不接受任何线程参数**：传 `thread` / `limit` / `max_thread` / `num` 一律无效，
服务端按账号档位固定下发。

## CDN 直链参数含义

```
https://201-usw-data.ctcontents.com/d37476991/<hash>/.txt
  ?cts=client-65856384     # 身份标识（匿名是 U0F<fid>D...）
  &ctp=113A65A32A205
  &ctt=1791208783          # 过期时间戳
  &limit=2                 # 允许的并发连接数
  &spd=100000              # 单连接限速（字节/秒，实测约 100 KB/s）
  &spd2=45000              # 超过 threshold 后的限速
  &threshold=5664102
  &ctk/&chk=...            # 签名
  &fname=...
```

## 已实测的结论（避坑）

1. **老登录接口已废弃**：`rest.ctfile.com/p2/user/auth/login` 对任何 `app_version` / 请求头
   都返回 `410 请升级客户端`。网上大量旧教程 / 旧项目还在用它，已经不能用了。

2. **节点不可切换**：只改直链的主机名（其余参数不动）换到其它节点，一律立刻 `503`。
   实测把非原始节点放第一个请求、间隔 12~20 秒重试仍然全 503，排除了并发限制的干扰。
   同一文件用不同账号（含匿名）取的直链也指向同一节点。

3. **并发按账号独立且相加**：同一账号开 4 条独立直链仍只有 2 条通；
   换成 2 个账号各 2 条 → 4/4 全通。所以 N 个账号 = 2N 线程。

4. **速度由 spd 决定，且多连接非线性叠加**：单连接实测快节点 104 KB/s、
   慢节点 32~34 KB/s；慢节点上 7 线程聚合只有 109 KB/s（约 3.3 倍，不是 7 倍）。

5. **IP 限流按接口主机分开算**：被限时 `webapi` / `rest` 返 429（空 body），
   而 `api.ctfile.com` 与分享页仍正常；恢复时间不同步。**批量探测几百次就会触发**。

6. **慢节点的账号入口会间歇性整片 503**：此时只有匿名那条（`tv002.com` 域名）能走，
   速度掉到 33 KB/s。遇到「突然变很慢」优先怀疑这个。

7. **下载器必须预检 + 剔除死链**：否则任一分块失败会导致整个下载报错。

## 未解 / 未验证

- `api.ctfile.com/v4/public/browser/*`（`validate` / `list` / `create-download-auth`）
  看起来是给分享页用的新接口，但试过 `url` / `code` / `link` 作为参数名都返回
  `api.public.missing_link`，没找到正确的参数名。破解它也许能摆脱 `webapi` 的限流。
- 文件夹分享（`/d/...`）没有实现，手里的样本都已失效。
- VIP 账号的 `limit` 是多少没验证过（猜测更高）。
