// Simple IRC bot over TLS: connect -> join #lobby -> say hi (as Valware) -> quit.
// Build: requires OpenSSL dev headers.
//   make
// Run:
//   ./irc_tls_bot [nick]

#include <arpa/inet.h>
#include <errno.h>
#include <netdb.h>
#include <netinet/in.h>
#include <openssl/err.h>
#include <openssl/ssl.h>
#include <openssl/x509v3.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <unistd.h>

#define IRC_HOST "irc.inthemansion.com"
#define IRC_PORT "6697" // TLS port
#define IRC_CHANNEL "#lobby"

static void die_ssl(const char *msg) {
    fprintf(stderr, "%s\n", msg);
    ERR_print_errors_fp(stderr);
    exit(1);
}

static int connect_tcp_tls(const char *host, const char *port, SSL_CTX **out_ctx, SSL **out_ssl) {
    struct addrinfo hints;
    struct addrinfo *res = NULL, *rp = NULL;
    memset(&hints, 0, sizeof(hints));
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_family = AF_UNSPEC;

    int gai = getaddrinfo(host, port, &hints, &res);
    if (gai != 0) {
        fprintf(stderr, "getaddrinfo: %s\n", gai_strerror(gai));
        return -1;
    }

    int fd = -1;
    for (rp = res; rp != NULL; rp = rp->ai_next) {
        fd = socket(rp->ai_family, rp->ai_socktype, rp->ai_protocol);
        if (fd < 0) continue;
        if (connect(fd, rp->ai_addr, rp->ai_addrlen) == 0) break;
        close(fd);
        fd = -1;
    }

    freeaddrinfo(res);
    if (fd < 0) {
        fprintf(stderr, "Failed to connect to %s:%s\n", host, port);
        return -1;
    }

    SSL_library_init();
    SSL_load_error_strings();
    const SSL_METHOD *method = TLS_client_method();
    SSL_CTX *ctx = SSL_CTX_new(method);
    if (!ctx) die_ssl("SSL_CTX_new failed");

    // Verify server certificate (default trust store)
    SSL_CTX_set_default_verify_paths(ctx);
    SSL_CTX_set_verify(ctx, SSL_VERIFY_PEER, NULL);

    SSL *ssl = SSL_new(ctx);
    if (!ssl) {
        SSL_CTX_free(ctx);
        die_ssl("SSL_new failed");
    }
    SSL_set_fd(ssl, fd);
    SSL_set_tlsext_host_name(ssl, host);

    if (SSL_connect(ssl) != 1) {
        SSL_free(ssl);
        SSL_CTX_free(ctx);
        die_ssl("SSL_connect failed");
    }

    *out_ctx = ctx;
    *out_ssl = ssl;
    return fd;
}

static int tls_write_all(SSL *ssl, const char *buf, size_t len) {
    size_t off = 0;
    while (off < len) {
        int n = SSL_write(ssl, buf + off, (int)(len - off));
        if (n <= 0) return -1;
        off += (size_t)n;
    }
    return 0;
}

static int tls_read_line(SSL *ssl, char *out, size_t out_sz, int timeout_ms) {
    // Reads until '\n' or buffer full. Returns 1 on success, 0 on timeout, -1 on error.
    size_t pos = 0;
    while (pos + 1 < out_sz) {
        // timeout using select on underlying fd
        int fd = SSL_get_fd(ssl);
        fd_set rfds;
        FD_ZERO(&rfds);
        FD_SET(fd, &rfds);
        struct timeval tv;
        tv.tv_sec = timeout_ms / 1000;
        tv.tv_usec = (timeout_ms % 1000) * 1000;

        int sel = select(fd + 1, &rfds, NULL, NULL, &tv);
        if (sel == 0) return 0;
        if (sel < 0) return -1;

        char c;
        int n = SSL_read(ssl, &c, 1);
        if (n <= 0) return -1;
        out[pos++] = c;
        if (c == '\n') {
            out[pos] = '\0';
            return 1;
        }
    }
    out[pos] = '\0';
    return 1;
}

static int send_irc_cmd(SSL *ssl, const char *fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);

    // Ensure CRLF
    size_t len = strlen(buf);
    char msg[1200];
    if (len >= 2 && buf[len - 2] == '\r' && buf[len - 1] == '\n') {
        snprintf(msg, sizeof(msg), "%s", buf);
        len = strlen(msg);
    } else {
        snprintf(msg, sizeof(msg), "%s\r\n", buf);
        len = strlen(msg);
    }

    return tls_write_all(ssl, msg, len);
}

static void strip_trailing_crlf(char *s) {
    size_t n = strlen(s);
    while (n > 0 && (s[n-1] == '\n' || s[n-1] == '\r')) {
        s[n-1] = 0;
        n--;
    }
}

int main(int argc, char **argv) {
    const char *nick = (argc >= 2 && argv[1] && argv[1][0]) ? argv[1] : "Valware";
    const char *username = "Valware";
    const char *realname = "Valware";

    SSL_CTX *ctx = NULL;
    SSL *ssl = NULL;
    int fd = connect_tcp_tls(IRC_HOST, IRC_PORT, &ctx, &ssl);
    if (fd < 0) return 1;

    // Identify (use a simple static nick/user).
    if (send_irc_cmd(ssl, "NICK %s", nick) < 0) die_ssl("send NICK failed");
    if (send_irc_cmd(ssl, "USER %s 0 * :%s", username, realname) < 0) die_ssl("send USER failed");

    int joined = 0;
    int quit_sent = 0;

    char line[2048];
    while (1) {
        int r = tls_read_line(ssl, line, sizeof(line), 15000);
        if (r == 0) {
            fprintf(stderr, "Timed out waiting for server messages\n");
            break;
        }
        if (r < 0) {
            int err = SSL_get_error(ssl, r);
            fprintf(stderr, "TLS read error (SSL_get_error=%d)\n", err);
            die_ssl("OpenSSL error stack (if any):");
            break;
        }

        strip_trailing_crlf(line);
        if (line[0] == '\0') continue;

        // Handle PING
        if (strncmp(line, "PING ", 5) == 0) {
            const char *payload = line + 5;
            if (send_irc_cmd(ssl, "PONG %s", payload) < 0) die_ssl("send PONG failed");
            continue;
        }

        // Once we receive welcome (001), join channel.
        if (!joined && strstr(line, " 001 ") != NULL) {
            if (send_irc_cmd(ssl, "JOIN %s", IRC_CHANNEL) < 0) die_ssl("send JOIN failed");
            joined = 1;
            continue;
        }

        // After join, say hi and quit.
        // Example JOIN line: :nick!user@host JOIN :#lobby
        if (joined && strstr(line, " JOIN ") != NULL && strstr(line, IRC_CHANNEL) != NULL) {
            // Send PRIVMSG as a greeting
            if (send_irc_cmd(ssl, "PRIVMSG %s :hi from %s", IRC_CHANNEL, realname) < 0) die_ssl("send PRIVMSG failed");
            if (send_irc_cmd(ssl, "QUIT :bye") < 0) die_ssl("send QUIT failed");
            quit_sent = 1;
            break;
        }

        // Fallback: if we already joined and see end of MOTD, also send message/quit.
        if (joined && strstr(line, " 376 ") != NULL && !quit_sent) {
            if (send_irc_cmd(ssl, "PRIVMSG %s :hi from %s", IRC_CHANNEL, realname) < 0) die_ssl("send PRIVMSG failed");
            if (send_irc_cmd(ssl, "QUIT :bye") < 0) die_ssl("send QUIT failed");
            quit_sent = 1;
            break;
        }
    }

    SSL_shutdown(ssl);
    SSL_free(ssl);
    SSL_CTX_free(ctx);
    close(fd);
    return 0;
}
