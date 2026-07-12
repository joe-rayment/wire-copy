// Control channel between the shell and the TUI child (the .NET app keeps ALL logic;
// the shell is a display surface it instructs). Transport: Unix domain socket, one
// JSON object per line. The child gets the socket path via WIRECOPY_SHELL_CHANNEL.
//
//   child → shell   {"id":1,"method":"hello","params":{}}
//   shell → child   {"id":1,"ok":true,"result":{"cdpEndpoint":"http://127.0.0.1:9223"}}
//   shell → child   {"event":"mode","params":{"mode":"browser"}}   (fire-and-forget)
const net = require('net')
const fs = require('fs')

function serve ({ socketPath, handlers, log = () => {} }) {
  try { fs.unlinkSync(socketPath) } catch {}
  const clients = new Set()

  const server = net.createServer(sock => {
    clients.add(sock)
    log(`channel: client connected (${clients.size})`)
    let buf = ''
    sock.on('data', chunk => {
      buf += chunk.toString('utf8')
      let nl
      while ((nl = buf.indexOf('\n')) >= 0) {
        const line = buf.slice(0, nl).trim()
        buf = buf.slice(nl + 1)
        if (!line) continue
        handleLine(sock, line)
      }
    })
    sock.on('close', () => { clients.delete(sock); log('channel: client disconnected') })
    sock.on('error', () => { clients.delete(sock) })
  })

  async function handleLine (sock, line) {
    let msg
    try { msg = JSON.parse(line) } catch { return }
    if (typeof msg.id !== 'number' || typeof msg.method !== 'string') return
    const reply = { id: msg.id }
    try {
      const handler = handlers[msg.method]
      if (!handler) throw new Error(`unknown method: ${msg.method}`)
      reply.ok = true
      reply.result = (await handler(msg.params || {})) || {}
    } catch (err) {
      reply.ok = false
      reply.error = String(err.message || err)
    }
    try { sock.write(JSON.stringify(reply) + '\n') } catch {}
  }

  server.listen(socketPath)

  return {
    socketPath,
    broadcast (event, params) {
      const line = JSON.stringify({ event, params }) + '\n'
      for (const c of clients) { try { c.write(line) } catch {} }
    },
    close () {
      for (const c of clients) { try { c.destroy() } catch {} }
      try { server.close() } catch {}
      try { fs.unlinkSync(socketPath) } catch {}
    }
  }
}

module.exports = { serve }
