// 城通网盘多来源并发下载器（命令行版，逻辑全部来自 core.mjs）
//
// 用法:
//   node 下载.mjs --sources "%TEMP%\ctfile-sources.json" "<保存路径>"
//   node 下载.mjs "<直链>" "<保存路径>" [线程数]
import { readFile } from 'node:fs/promises'
import { downloadTo } from './core.mjs'

const argv = process.argv.slice(2)
const srcIdx = argv.indexOf('--sources')
let sources = []
let out = ''
let manualThreads = 0

if (srcIdx >= 0) {
  const file = argv[srcIdx + 1]
  out = argv[srcIdx + 2]
  if (!file || !out) {
    console.error('用法: node 下载.mjs --sources "<sources.json>" "<保存路径>"')
    process.exit(1)
  }
  const j = JSON.parse(await readFile(file, 'utf8'))
  sources = (j.sources || []).filter((s) => s && s.url)
} else {
  const pos = argv.filter((a) => !a.startsWith('--'))
  const url = pos[0]
  out = pos[1]
  manualThreads = Number(pos[2]) || 0
  if (!url || !out) {
    console.error('用法: node 下载.mjs "<直链>" "<保存路径>" [线程数]')
    process.exit(1)
  }
  sources = [{ url, threads: 1, label: '直链' }]
}

if (!sources.length) {
  console.error('❌ 没有可用的直链来源')
  process.exit(1)
}

// 手动指定线程数时，按直链里的 limit 收敛
if (manualThreads) {
  for (const s of sources) {
    let lim = 1
    try { lim = Number(new URL(s.url).searchParams.get('limit')) || 1 } catch {}
    s.threads = Math.max(1, Math.min(manualThreads, lim))
  }
}

const fmt = (n) => (n >= 1048576 ? (n / 1048576).toFixed(1) + 'MB' : (n / 1024).toFixed(0) + 'KB')

const totalThreads = sources.reduce((a, s) => a + Math.max(1, s.threads || 1), 0)
console.log(`${sources.length} 条直链 | ${totalThreads} 线程并发`)
for (const s of sources) console.log(`   · ${String(s.label || '?').padEnd(28)} ${Math.max(1, s.threads || 1)} 线程`)

let lastPct = -1
try {
  const r = await downloadTo(sources, out, (p) => {
    const pct = p.total ? Math.floor((p.done / p.total) * 100) : 0
    if (pct !== lastPct) {
      lastPct = pct
      process.stdout.write(`\r  进度 ${pct}%  ${fmt(p.done)}/${fmt(p.total)}  ${fmt(p.speed)}/s   `)
    }
  })
  process.stdout.write('\r' + ' '.repeat(70) + '\r')
  console.log(`✅ 下载完成  ${fmt(r.bytes)}  用时 ${r.seconds.toFixed(1)}s  平均 ${fmt(r.bytes / r.seconds)}/s`)
  console.log(`   保存到: ${r.outPath}`)
} catch (e) {
  process.stdout.write('\r' + ' '.repeat(70) + '\r')
  console.error(`❌ 下载失败：${e.message}`)
  process.exit(1)
}
