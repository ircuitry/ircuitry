CC ?= cc
CFLAGS ?= -O2 -Wall -Wextra -pedantic
LDFLAGS ?= -lssl -lcrypto

all: irc_tls_bot

irc_tls_bot: irc_tls_bot.c
	$(CC) $(CFLAGS) -o $@ $^ $(LDFLAGS)

clean:
	rm -f irc_tls_bot
