#!/usr/bin/env python3
"""Forward proxy for the extension gate (python3 stdlib only).

Usage: python3 proxy.py <port>      (listens on 127.0.0.1:<port>)

* CONNECT host:port          -> TCP tunnel to the real host (HTTPS).
* GET http://host/path ...   -> forwarded to the real host (plain HTTP).
* Requests for host gazette.wirecopy.test (plain HTTP) are NOT forwarded; they
  are served from fixture/site/ next to this script. "/" and any path whose last
  segment has no extension map to index.html inside that folder.
* One log line per request on stdout:  <METHOD> <host> <path> <local|forwarded>

This process is the browser's ONLY route to the internet. Killing it = offline.
"""
import asyncio
import mimetypes
import os
import sys
import urllib.parse

HERE = os.path.dirname(os.path.abspath(__file__))
SITE_ROOT = os.path.join(HERE, "fixture", "site")
LOCAL_HOST = "gazette.wirecopy.test"
MAX_HEAD = 64 * 1024


def log(method, host, path, mode):
    print(f"{method} {host} {path} {mode}", flush=True)


def content_type(path):
    if path.endswith((".html", ".htm")):
        return "text/html; charset=utf-8"
    ctype, _ = mimetypes.guess_type(path)
    if ctype is None:
        return "application/octet-stream"
    if ctype.startswith("text/") or ctype in ("application/json", "application/javascript"):
        return ctype + "; charset=utf-8"
    return ctype


def resolve_local(url_path):
    """Map a URL path to a file under SITE_ROOT, or None."""
    path = urllib.parse.unquote(urllib.parse.urlsplit(url_path).path)
    parts = [p for p in path.split("/") if p not in ("", ".")]
    if any(p == ".." for p in parts):
        return None
    last = parts[-1] if parts else ""
    if path.endswith("/") or not parts or "." not in last:
        candidate = os.path.join(SITE_ROOT, *parts, "index.html")
    else:
        candidate = os.path.join(SITE_ROOT, *parts)
    candidate = os.path.realpath(candidate)
    if not candidate.startswith(os.path.realpath(SITE_ROOT) + os.sep):
        return None
    return candidate if os.path.isfile(candidate) else None


async def respond(writer, status, reason, body=b"", ctype="text/plain; charset=utf-8", head_only=False):
    head = (
        f"HTTP/1.1 {status} {reason}\r\n"
        f"Content-Type: {ctype}\r\n"
        f"Content-Length: {len(body)}\r\n"
        "Cache-Control: no-store\r\n"
        "Connection: close\r\n\r\n"
    ).encode("latin-1")
    writer.write(head + (b"" if head_only else body))
    await writer.drain()


async def pipe(reader, writer):
    try:
        while True:
            data = await reader.read(65536)
            if not data:
                break
            writer.write(data)
            await writer.drain()
    except (ConnectionError, asyncio.CancelledError, OSError):
        pass
    finally:
        try:
            writer.close()
        except Exception:
            pass


def split_host_port(hostport, default_port):
    hostport = hostport.strip()
    if hostport.startswith("["):
        host, _, rest = hostport[1:].partition("]")
        port = int(rest[1:]) if rest.startswith(":") and rest[1:] else default_port
        return host, port
    if hostport.count(":") == 1:
        host, _, port = hostport.partition(":")
        return host, int(port) if port else default_port
    return hostport, default_port


async def handle(client_reader, client_writer):
    upstream_writer = None
    try:
        head = await client_reader.readuntil(b"\r\n\r\n")
        if len(head) > MAX_HEAD:
            return
        lines = head[:-4].decode("latin-1").split("\r\n")
        method, target, version = (lines[0].split(" ") + ["", ""])[:3]
        headers = [ln.split(":", 1) for ln in lines[1:] if ":" in ln]
        host_header = next((v.strip() for k, v in headers if k.lower() == "host"), "")

        if method.upper() == "CONNECT":
            host, port = split_host_port(target, 443)
            if host.lower() == LOCAL_HOST:
                log("CONNECT", target, "/", "local-refused")
                await respond(client_writer, 502, "Bad Gateway", b"gazette.wirecopy.test is plain-HTTP only\n")
                return
            log("CONNECT", target, "/", "forwarded")
            try:
                up_reader, upstream_writer = await asyncio.wait_for(asyncio.open_connection(host, port), 15)
            except Exception as exc:
                await respond(client_writer, 502, "Bad Gateway", f"cannot reach {target}: {exc}\n".encode())
                return
            client_writer.write(b"HTTP/1.1 200 Connection Established\r\n\r\n")
            await client_writer.drain()
            await asyncio.gather(pipe(client_reader, upstream_writer), pipe(up_reader, client_writer))
            return

        # Plain HTTP: absolute-form (proxy request) or origin-form with Host header.
        if target.lower().startswith("http://"):
            u = urllib.parse.urlsplit(target)
            hostport = u.netloc
            path = u.path or "/"
            if u.query:
                path += "?" + u.query
        else:
            hostport = host_header
            path = target
        host, port = split_host_port(hostport, 80)

        if host.lower() == LOCAL_HOST:
            log(method, host, path, "local")
            if method.upper() not in ("GET", "HEAD"):
                await respond(client_writer, 405, "Method Not Allowed", b"read-only fixture site\n")
                return
            file_path = resolve_local(path)
            if file_path is None:
                await respond(client_writer, 404, "Not Found", f"no fixture for {path}\n".encode())
                return
            with open(file_path, "rb") as f:
                body = f.read()
            await respond(client_writer, 200, "OK", body, content_type(file_path), head_only=method.upper() == "HEAD")
            return

        log(method, host, path, "forwarded")
        try:
            up_reader, upstream_writer = await asyncio.wait_for(asyncio.open_connection(host, port), 15)
        except Exception as exc:
            await respond(client_writer, 502, "Bad Gateway", f"cannot reach {host}:{port}: {exc}\n".encode())
            return
        # Rewrite to origin-form, one request per upstream connection.
        out = [f"{method} {path} {version}"]
        for k, v in headers:
            if k.lower() in ("proxy-connection", "connection", "keep-alive", "proxy-authorization"):
                continue
            out.append(f"{k}:{v}")
        out.append("Connection: close")
        upstream_writer.write(("\r\n".join(out) + "\r\n\r\n").encode("latin-1"))
        await upstream_writer.drain()
        await asyncio.gather(pipe(client_reader, upstream_writer), pipe(up_reader, client_writer))
    except (asyncio.IncompleteReadError, asyncio.LimitOverrunError, ConnectionError, OSError):
        pass
    finally:
        for w in (client_writer, upstream_writer):
            if w is not None:
                try:
                    w.close()
                except Exception:
                    pass


async def main(port):
    server = await asyncio.start_server(handle, "127.0.0.1", port, limit=MAX_HEAD)
    print(f"proxy listening on 127.0.0.1:{port}", flush=True)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("usage: proxy.py <port>")
    try:
        asyncio.run(main(int(sys.argv[1])))
    except KeyboardInterrupt:
        pass
