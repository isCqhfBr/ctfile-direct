// 城通网盘下载核心逻辑（命令行版）
import { readFileSync, writeFileSync, existsSync } from 'node:fs'
import { open, stat } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

export const HERE = dirname(fileURLToPath(import.meta.url))
// config.json 与 exe 共用：先找本目录，找不到就用上一级目录的
export const CONFIG_PATH = (() => {
  const local = join(HERE, 'config.json')
  if (existsSync(local)) return local
  return join(HERE, '..', 'config.json')
})()

const WEBAPI = 'https://webapi.ctfile.com'
const REST = 'https://rest.ctfile.com'
const API = 'https://api.ctfile.com'
export const UA =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36'
const OKHTTP = 'okhttp/4.9.2'

// ---------------- 配置 ----------------
export function loadConfig() {
  if (!existsSync(CONFIG_PATH)) return { useAnonymous: true, accounts: [] }
  try {
    const c = JSON.parse(readFileSync(CONFIG_PATH, 'utf8'))
    const accounts = Array.isArray(c.accounts)
      ? c.accounts.filter((a) => a && a.email && a.password)
      : c.email && c.password
        ? [{ email: c.email, password: c.password }]
        : []
    return { useAnonymous: c.useAnonymous !== false, accounts }
  } catch {
    return { useAnonymous: true, accounts: [] }
  }
}

export function saveConfig(cfg) {
  const out = {
    _说明: '城通网盘账号池。每个免费账号 2 个并发连接，N 个账号 = 2N 并发；匿名槽额外 +1（useAnonymous）。密码明文存储。',
    _风险: '城通风控会盯「同 IP 多账号下载同一文件」，被判定多用户登录的账号会被锁定。',
    useAnonymous: cfg.useAnonymous !== false,
    accounts: (cfg.accounts || []).filter((a) => a && a.email && a.password),
  }
  writeFileSync(CONFIG_PATH, JSON.stringify(out, null, 2), 'utf8')
}

// ---------------- 解析分享链接 ----------------
export function parseShareInput(input) {
  const s = String(input || '').trim()
  if (!s) throw new Error('请先粘贴城通分享链接')
  const extract = (t) => {
    const m = t.match(/\/([fd]l?)\/([0-9]+-[0-9]+-[0-9a-fA-F]+)/)
    if (m) return { kind: m[1][0], code: m[2] }
    const m2 = t.match(/^([0-9]+-[0-9]+-[0-9a-fA-F]+)$/)
    if (m2) return { kind: 'f', code: m2[1] }
    return null
  }
  if (/^https?:\/\//i.test(s)) {
    let u
    try { u = new URL(s) } catch { throw new Error('链接格式不对，请检查是否复制完整') }
    const f = extract(u.pathname)
    if (!f) throw new Error('看不懂这个链接，需要形如 https://url91.ctfile.com/f/37476991-1447877437-0eb71b 的分享链接')
    if (f.kind !== 'f') throw new Error('目前只支持单文件分享（/f/...），文件夹分享（/d/...）暂未支持')
    return {
      code: f.code,
      passcode: u.searchParams.get('p') || u.searchParams.get('passcode') || '',
      pageUrl: s,
    }
  }
  const f = extract(s)
  if (!f) throw new Error('看不懂这个分享码，应为 37476991-1447877437-0eb71b 这种格式')
  if (f.kind !== 'f') throw new Error('目前只支持单文件分享（/f/...）')
  return { code: f.code, passcode: '', pageUrl: `https://url91.ctfile.com/f/${f.code}` }
}

async function getMeta(p) {
  const url =
    `${WEBAPI}/getfile.php?path=f&f=${encodeURIComponent(p.code)}` +
    `&passcode=${encodeURIComponent(p.passcode)}&r=${Math.random()}` +
    `&ref=&url=${encodeURIComponent(p.pageUrl)}`
  const data = await fetchJson(url, {
    headers: { 'User-Agent': UA, 'X-Requested-With': 'XMLHttpRequest', Referer: p.pageUrl, Accept: 'application/json, text/javascript, */*; q=0.01' },
  }, '取文件信息接口')
  if (data.code === 423) throw new Error('这个分享需要提取码，请在链接后面加 ?p=提取码')
  if (data.code !== 200 || !data.file) {
    throw new Error(`取文件信息失败：${data.message || 'code=' + data.code}（提取码错了 / 分享已失效都会这样）`)
  }
  return { meta: data.file, headers: { 'User-Agent': UA, 'X-Requested-With': 'XMLHttpRequest', Referer: p.pageUrl } }
}

// 城通会按接口主机限流（短时间请求太多就返 429）。统一做退避重试 + 人话报错。
async function fetchJson(url, opts, what) {
  const waits = [2000, 5000]
  for (let attempt = 0; ; attempt++) {
    let r
    try {
      r = await fetch(url, { ...opts, signal: AbortSignal.timeout(25000) })
    } catch (e) {
      throw new Error(`${what} 请求失败：${e.message}`)
    }
    if (r.status === 429) {
      if (attempt < waits.length) {
        await new Promise((s) => setTimeout(s, waits[attempt]))
        continue
      }
      throw new Error('城通把这个 IP 限流了（HTTP 429，请求过于频繁）。\n这不是链接失效，等 10~30 分钟再试就行。')
    }
    const t = await r.text()
    if (!t) throw new Error(`${what} 返回了空响应（HTTP ${r.status}），可能是被限流，稍后再试。`)
    try {
      return JSON.parse(t)
    } catch {
      throw new Error(`${what} 返回了非 JSON 内容（HTTP ${r.status}）。`)
    }
  }
}

async function login(email, password) {
  const j = await fetchJson(`${API}/v4/user/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'User-Agent': UA, Origin: 'https://my.ctfile.com', Referer: 'https://my.ctfile.com/' },
    body: JSON.stringify({ email, password }),
  }, '登录接口')
  if (j.code !== 200 || !j.data?.token) {
    const msg = j.message || `code=${j.code}`
    if (/锁定|locked/i.test(msg)) throw new Error(`账号被风控锁定（${msg}）`)
    throw new Error(msg)
  }
  return { token: j.data.token, uid: j.data.user?.userid, group: j.data.user?.group_name }
}

const p2 = async (ep, body) => fetchJson(REST + ep, {
  method: 'POST',
  headers: { 'User-Agent': OKHTTP, 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
}, '客户端接口')

/**
 * 解析分享，返回每个来源一条直链。
 * @returns {{fileName,fileSize,sources:Array,warnings:Array,accountsInfo:Array}}
 */
export async function resolveShare(input) {
  const parsed = parseShareInput(input)
  const { meta, headers } = await getMeta(parsed)
  const cfg = loadConfig()
  const warnings = []
  const accountsInfo = []
  const sources = []
  let fileName = meta.file_name
  let fileSize = null

  if (cfg.accounts.length && meta.xtredirect) {
    const xtlink = 'ctfile://' + meta.xtredirect
    let list = null
    for (const acc of cfg.accounts) {
      try {
        const { token, uid, group } = await login(acc.email, acc.password)
        if (!list) {
          const res = await p2('/p2/browser/file/list', { xtlink, token, reload: false })
          if (res.code !== 200 || !Array.isArray(res.results)) {
            warnings.push(`客户端接口返回 ${res.code}：${res.message || ''}`)
            break
          }
          list = res.results
        }
        const item = list.find((it) => it.icon !== 'folder')
        if (!item) { warnings.push('分享里没有可下载的文件'); break }
        const dl = await p2('/p2/browser/file/fetch_url', { xtlink, file_id: item.key, token })
        if (dl.code === 200 && dl.download_url) {
          const threads = Number(new URL(dl.download_url).searchParams.get('limit')) || 2
          sources.push({ url: dl.download_url, threads, label: acc.email, kind: 'account', host: new URL(dl.download_url).host })
          accountsInfo.push({ email: acc.email, uid, group, threads, ok: true })
          fileName = item.name || fileName
          fileSize = item.size || fileSize
        } else {
          warnings.push(`${acc.email} 取直链失败：${dl.message || 'code=' + dl.code}`)
          accountsInfo.push({ email: acc.email, uid, group, ok: false, error: dl.message || 'code=' + dl.code })
        }
      } catch (e) {
        warnings.push(`${acc.email}：${e.message}`)
        accountsInfo.push({ email: acc.email, ok: false, error: e.message })
      }
    }
  } else if (cfg.accounts.length && !meta.xtredirect) {
    warnings.push('本次分享没有返回 xtredirect，无法走客户端接口')
  }

  if (cfg.useAnonymous) {
    try {
      const q = new URLSearchParams({
        uid: String(meta.userid), fid: String(meta.file_id), file_chk: meta.file_chk,
        start_time: String(meta.start_time ?? 0), wait_seconds: String(meta.wait_seconds ?? 0), rd: String(Math.random()),
      })
      const data = await fetchJson(`${WEBAPI}/get_down_url.php?${q}`, { headers }, '匿名直链接口')
      if (data.downurl) {
        const threads = Number(new URL(data.downurl).searchParams.get('limit')) || 1
        sources.push({ url: data.downurl, threads, label: '匿名', kind: 'anonymous', host: new URL(data.downurl).host })
        fileSize = fileSize || Number(data.file_size) || null
      }
    } catch (e) {
      warnings.push('匿名直链获取失败：' + e.message)
    }
  }

  if (!sources.length) throw new Error(warnings.length ? warnings.join('；') : '没有拿到任何可用直链')

  return { fileName, fileSize, sources, warnings, accountsInfo, totalThreads: sources.reduce((s, x) => s + x.threads, 0) }
}

// ---------------- 多来源并发下载 ----------------
/**
 * @param sources [{url, threads, label}]
 * @param outPath 保存路径
 * @param onProgress (p) => void  p={done,total,speed,perSource:[{label,done,total}]}
 */
export async function downloadTo(sources, outPath, onProgress = () => {}, signal) {
  const fmtSources = sources.filter((s) => s && s.url)
  if (!fmtSources.length) throw new Error('没有可用的直链来源')

  let size = 0
  for (const s of fmtSources) {
    try {
      const r = await fetch(s.url, { method: 'HEAD', headers: { 'User-Agent': OKHTTP }, signal: AbortSignal.timeout(20000) })
      const len = Number(r.headers.get('content-length')) || 0
      if (len) { size = len; break }
    } catch {}
  }
  if (!size) throw new Error('拿不到文件大小，直链可能已过期，请重新解析')

  const workers = []
  fmtSources.forEach((s, si) => {
    const t = Math.max(1, Math.min(s.threads || 1, 8))
    for (let i = 0; i < t; i++) workers.push({ url: s.url, si })
  })
  const N = workers.length
  const chunk = Math.ceil(size / N)

  const fh = await open(outPath, 'w')
  let done = 0
  let lastBytes = 0
  let lastTick = Date.now()
  const perSource = fmtSources.map((s) => ({ label: s.label, done: 0, total: 0 }))
  workers.forEach((w, i) => {
    const start = i * chunk
    const end = Math.min(start + chunk - 1, size - 1)
    if (start <= end) perSource[w.si].total += end + 1 - start
  })

  const tick = () => {
    const now = Date.now()
    const speed = (done - lastBytes) / ((now - lastTick) / 1000)
    lastBytes = done; lastTick = now
    onProgress({ done, total: size, speed, perSource, threads: N })
  }
  const timer = setInterval(tick, 500)
  const t0 = Date.now()

  async function fetchRange(url, start, end, si, attempt = 1) {
    if (signal?.aborted) throw new Error('已取消')
    try {
      const r = await fetch(url, { headers: { 'User-Agent': OKHTTP, Range: `bytes=${start}-${end}` }, signal })
      if (r.status !== 206 && r.status !== 200) throw new Error('HTTP ' + r.status)
      let pos = start
      for await (const c of r.body) {
        const buf = Buffer.from(c)
        await fh.write(buf, 0, buf.length, pos)
        pos += buf.length
        done += buf.length
        perSource[si].done += buf.length
      }
      if (pos !== end + 1) throw new Error(`分块不完整 ${pos - start}/${end + 1 - start}`)
    } catch (e) {
      if (signal?.aborted) throw new Error('已取消')
      if (attempt < 4) {
        await new Promise((res) => setTimeout(res, 800 * attempt))
        return fetchRange(url, start, end, si, attempt + 1)
      }
      throw e
    }
  }

  try {
    await Promise.all(workers.map((w, i) => {
      const start = i * chunk
      const end = Math.min(start + chunk - 1, size - 1)
      return start > end ? Promise.resolve() : fetchRange(w.url, start, end, w.si)
    }))
    clearInterval(timer)
    tick()
    await fh.close()
    const st = await stat(outPath)
    if (st.size !== size) throw new Error(`文件大小不符：期望 ${size}，实际 ${st.size}`)
    return { bytes: size, seconds: (Date.now() - t0) / 1000, outPath, threads: N }
  } catch (e) {
    clearInterval(timer)
    await fh.close().catch(() => {})
    throw e
  }
}
