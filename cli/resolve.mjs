// 城通网盘分享 -> 直链（命令行版，逻辑全部来自 core.mjs）
// 用法: node resolve.mjs "<分享链接或分享码>" [--url-only] [--json]
import { writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { resolveShare } from './core.mjs'

const argv = process.argv.slice(2)
const urlOnly = argv.includes('--url-only')
const asJson = argv.includes('--json')
const input = argv.find((a) => !a.startsWith('--'))

function fail(msg, code = 1) {
  if (asJson) console.log(JSON.stringify({ ok: false, error: msg }))
  else console.error('❌ ' + msg)
  process.exit(code)
}

if (!input) fail('缺少分享链接。用法: node resolve.mjs "https://url91.ctfile.com/f/xxx?p=提取码"')

let r
try {
  r = await resolveShare(input)
} catch (e) {
  fail(e.message)
}

const primary = r.sources[0].url
{
  const tmp = tmpdir()
  writeFileSync(join(tmp, 'ctfile-url.txt'), primary, 'utf8')
  writeFileSync(join(tmp, 'ctfile-name.txt'), r.fileName || 'ctfile-download.bin', 'utf8')
  writeFileSync(join(tmp, 'ctfile-sources.json'), JSON.stringify({ fileName: r.fileName, fileSize: r.fileSize, sources: r.sources }, null, 2), 'utf8')
}

if (asJson) {
  console.log(JSON.stringify({ ok: true, fileName: r.fileName, fileSize: r.fileSize, totalThreads: r.totalThreads, sources: r.sources, warnings: r.warnings }))
} else if (urlOnly) {
  console.log(primary)
} else {
  console.log('')
  console.log('  文件名 : ' + r.fileName)
  if (r.fileSize) console.log('  大小   : ' + (r.fileSize / 1048576).toFixed(2) + ' MB')
  console.log('  直链来源：')
  for (const s of r.sources) console.log('    · ' + String(s.label).padEnd(30) + s.threads + ' 线程  ' + s.host)
  console.log('')
  console.log(`  ⚡ 合计 ${r.sources.length} 条直链 / ${r.totalThreads} 线程并发`)
  for (const w of r.warnings) console.log('  ⚠️  ' + w)
  console.log('  ✅ 主直链已复制到剪贴板')
  console.log('  ⏱  链接有效期约 6 小时，过期重新解析即可')
  console.log('')
}
