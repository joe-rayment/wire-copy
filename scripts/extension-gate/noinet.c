// LD_PRELOAD shim that simulates "the internet is off" for the extension gate.
//
// Interposes getaddrinfo() (see below) plus connect(), sendto(), sendmsg() (and
// sendmmsg()). For AF_INET /
// AF_INET6 destinations that are NOT loopback (127.0.0.0/8, ::1, ::ffff:127.x)
// the call fails with ENETUNREACH and returns -1. Loopback passes through.
// AF_UNIX and everything else is untouched.
//
// Every AF_INET/AF_INET6 attempt (allowed or blocked) appends one line to the
// file named by $NOINET_LOG (if set):
//   pid=<pid> exe=<exe> call=<connect|sendto|sendmsg|sendmmsg> dst=<ip>:<port> verdict=<allow|block>
// Logging uses open(O_APPEND)+write+close, no stdio, and ignores failures
// (sandboxed browser child processes cannot write files).
//
// Build: ./make.sh   Use: LD_PRELOAD=/abs/path/noinet.so NOINET_LOG=/tmp/x.log <cmd>

#define _GNU_SOURCE
#include <arpa/inet.h>
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <netdb.h>
#include <netinet/in.h>
#include <stdlib.h>
#include <strings.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <unistd.h>

static int is_loopback(const struct sockaddr *sa, char *ipbuf, size_t ipbuf_len, unsigned *port)
{
    if (sa->sa_family == AF_INET) {
        const struct sockaddr_in *in4 = (const struct sockaddr_in *)sa;
        inet_ntop(AF_INET, &in4->sin_addr, ipbuf, (socklen_t)ipbuf_len);
        *port = ntohs(in4->sin_port);
        return (ntohl(in4->sin_addr.s_addr) >> 24) == 127;
    }
    const struct sockaddr_in6 *in6 = (const struct sockaddr_in6 *)sa;
    inet_ntop(AF_INET6, &in6->sin6_addr, ipbuf, (socklen_t)ipbuf_len);
    *port = ntohs(in6->sin6_port);
    if (IN6_IS_ADDR_LOOPBACK(&in6->sin6_addr)) {
        return 1;
    }
    if (IN6_IS_ADDR_V4MAPPED(&in6->sin6_addr)) {
        return in6->sin6_addr.s6_addr[12] == 127;
    }
    return 0;
}

static size_t append_str(char *buf, size_t pos, size_t cap, const char *s)
{
    while (*s && pos + 1 < cap) {
        buf[pos++] = *s++;
    }
    return pos;
}

static size_t append_num(char *buf, size_t pos, size_t cap, unsigned long v)
{
    char tmp[24];
    size_t n = 0;
    do {
        tmp[n++] = (char)('0' + (v % 10));
        v /= 10;
    } while (v && n < sizeof tmp);
    while (n && pos + 1 < cap) {
        buf[pos++] = tmp[--n];
    }
    return pos;
}

static void log_line(const char *call, const char *ip, unsigned port, int allowed)
{
    const char *path = getenv("NOINET_LOG");
    if (!path || !*path) {
        return;
    }
    char exe[256] = "?";
    ssize_t n = readlink("/proc/self/exe", exe, sizeof exe - 1);
    if (n > 0) {
        exe[n] = 0;
    }
    char line[640];
    size_t p = 0;
    const size_t cap = sizeof line;
    p = append_str(line, p, cap, "pid=");
    p = append_num(line, p, cap, (unsigned long)getpid());
    p = append_str(line, p, cap, " exe=");
    p = append_str(line, p, cap, exe);
    p = append_str(line, p, cap, " call=");
    p = append_str(line, p, cap, call);
    p = append_str(line, p, cap, " dst=");
    p = append_str(line, p, cap, ip);
    p = append_str(line, p, cap, ":");
    p = append_num(line, p, cap, port);
    p = append_str(line, p, cap, allowed ? " verdict=allow\n" : " verdict=block\n");
    int fd = open(path, O_WRONLY | O_APPEND | O_CREAT, 0644);
    if (fd >= 0) {
        ssize_t w = write(fd, line, p);
        (void)w;
        close(fd);
    }
}

// glibc resolves names through its own internal DNS client, which does not go
// through our connect()/sendto() hooks, so name lookups would be invisible.
// getaddrinfo() is therefore interposed too: only node == NULL, "localhost"
// and numeric loopback literals resolve; everything else fails with EAI_FAIL.
// Log line: pid=<pid> exe=<exe> call=getaddrinfo name=<node> verdict=<allow|block>
static void log_name(const char *node, int allowed)
{
    const char *path = getenv("NOINET_LOG");
    if (!path || !*path) {
        return;
    }
    char exe[256] = "?";
    ssize_t n = readlink("/proc/self/exe", exe, sizeof exe - 1);
    if (n > 0) {
        exe[n] = 0;
    }
    char line[640];
    size_t p = 0;
    const size_t cap = sizeof line;
    p = append_str(line, p, cap, "pid=");
    p = append_num(line, p, cap, (unsigned long)getpid());
    p = append_str(line, p, cap, " exe=");
    p = append_str(line, p, cap, exe);
    p = append_str(line, p, cap, " call=getaddrinfo name=");
    p = append_str(line, p, cap, node ? node : "(null)");
    p = append_str(line, p, cap, allowed ? " verdict=allow\n" : " verdict=block\n");
    int fd = open(path, O_WRONLY | O_APPEND | O_CREAT, 0644);
    if (fd >= 0) {
        ssize_t w = write(fd, line, p);
        (void)w;
        close(fd);
    }
}

static int name_is_loopback(const char *node)
{
    if (!node) {
        return 1;
    }
    if (strcasecmp(node, "localhost") == 0) {
        return 1;
    }
    struct in_addr a4;
    if (inet_pton(AF_INET, node, &a4) == 1) {
        return (ntohl(a4.s_addr) >> 24) == 127;
    }
    struct in6_addr a6;
    if (inet_pton(AF_INET6, node, &a6) == 1) {
        if (IN6_IS_ADDR_LOOPBACK(&a6)) {
            return 1;
        }
        return IN6_IS_ADDR_V4MAPPED(&a6) && a6.s6_addr[12] == 127;
    }
    return 0;
}

int getaddrinfo(const char *node, const char *service, const struct addrinfo *hints, struct addrinfo **res)
{
    static int (*real)(const char *, const char *, const struct addrinfo *, struct addrinfo **);
    if (!real) {
        real = dlsym(RTLD_NEXT, "getaddrinfo");
    }
    int allowed = name_is_loopback(node);
    if (node) {
        log_name(node, allowed);
    }
    if (!allowed) {
        return EAI_FAIL;
    }
    return real(node, service, hints, res);
}

// Returns 1 if the call may proceed, 0 if it must be refused (errno set).
static int check(const char *call, const struct sockaddr *sa, socklen_t len)
{
    if (!sa || len < sizeof(sa_family_t)) {
        return 1;
    }
    if (sa->sa_family == AF_INET && len < sizeof(struct sockaddr_in)) {
        return 1;
    }
    if (sa->sa_family == AF_INET6 && len < sizeof(struct sockaddr_in6)) {
        return 1;
    }
    if (sa->sa_family != AF_INET && sa->sa_family != AF_INET6) {
        return 1;
    }
    int saved = errno;
    char ip[64] = "?";
    unsigned port = 0;
    int allowed = is_loopback(sa, ip, sizeof ip, &port);
    log_line(call, ip, port, allowed);
    if (allowed) {
        errno = saved;
        return 1;
    }
    errno = ENETUNREACH;
    return 0;
}

int connect(int fd, const struct sockaddr *addr, socklen_t len)
{
    static int (*real)(int, const struct sockaddr *, socklen_t);
    if (!real) {
        real = dlsym(RTLD_NEXT, "connect");
    }
    if (!check("connect", addr, len)) {
        return -1;
    }
    return real(fd, addr, len);
}

ssize_t sendto(int fd, const void *buf, size_t n, int flags, const struct sockaddr *addr, socklen_t alen)
{
    static ssize_t (*real)(int, const void *, size_t, int, const struct sockaddr *, socklen_t);
    if (!real) {
        real = dlsym(RTLD_NEXT, "sendto");
    }
    if (!check("sendto", addr, alen)) {
        return -1;
    }
    return real(fd, buf, n, flags, addr, alen);
}

ssize_t sendmsg(int fd, const struct msghdr *msg, int flags)
{
    static ssize_t (*real)(int, const struct msghdr *, int);
    if (!real) {
        real = dlsym(RTLD_NEXT, "sendmsg");
    }
    if (msg && msg->msg_name && !check("sendmsg", (const struct sockaddr *)msg->msg_name, msg->msg_namelen)) {
        return -1;
    }
    return real(fd, msg, flags);
}

int sendmmsg(int fd, struct mmsghdr *msgvec, unsigned int vlen, int flags)
{
    static int (*real)(int, struct mmsghdr *, unsigned int, int);
    if (!real) {
        real = dlsym(RTLD_NEXT, "sendmmsg");
    }
    for (unsigned int i = 0; msgvec && i < vlen; i++) {
        struct msghdr *m = &msgvec[i].msg_hdr;
        if (m->msg_name && !check("sendmmsg", (const struct sockaddr *)m->msg_name, m->msg_namelen)) {
            return -1;
        }
    }
    return real(fd, msgvec, vlen, flags);
}
