/*
 * jctool-hidraw: the Joy-Con Toolkit's Bluetooth bridge on Android (and any Linux).
 *
 * Android's Bluetooth service connects a paired Joy-Con / Pro Controller and hands it to
 * the kernel (uhid), which makes a /dev/hidrawN for it; only root can open that. The app
 * runs this helper through su, and talks to it over its stdin / stdout:
 *
 *   jctool-hidraw list
 *       One line per HID device:
 *       id \t hidraw \t bus \t vendor \t product \t driver \t name \t uniq
 *       (id: the kernel's HID device name, e.g. 0005:057E:2007.0003; bus 0005: Bluetooth)
 *
 *   jctool-hidraw open <id> [--keep-driver]
 *       Opens the device's hidraw node. If the kernel's hid_nintendo driver has it (it
 *       would talk to the controller at the same time), it is moved to hid-generic while
 *       open, and given back at exit (--keep-driver: don't touch the driver).
 *       Then frames, both ways: type (1 byte), length (2 bytes, little endian), payload.
 *         app -> helper   'W' output report    'Q' quit
 *         helper -> app   'O' opened (payload: the hidraw node)
 *                         'I' input report     'E' error text (then exits)
 *
 * JCTOOL_HIDRAW_ROOT=<dir> prefixes /sys and /dev (tests). A device node that is a unix
 * SOCK_SEQPACKET socket is connected to instead of opened (tests: an emulated controller).
 *
 * Build (NDK): <ndk>/toolchains/llvm/prebuilt/<host>/bin/aarch64-linux-android24-clang
 *              -O2 -o jctool-hidraw jctool_hidraw.c
 */
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <signal.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <time.h>
#include <unistd.h>

#define MAX_REPORT 1024

static char root[PATH_MAX];

static void path_of(char *out, size_t size, const char *fmt, ...)
{
	char tail[PATH_MAX];
	va_list ap;
	va_start(ap, fmt);
	vsnprintf(tail, sizeof(tail), fmt, ap);
	va_end(ap);
	snprintf(out, size, "%s%s", root, tail);
}

static int read_text(const char *path, char *buf, size_t size)
{
	FILE *f = fopen(path, "r");
	size_t n;
	if (!f)
		return -1;
	n = fread(buf, 1, size - 1, f);
	fclose(f);
	buf[n] = 0;
	return (int)n;
}

static int write_text(const char *path, const char *text)
{
	int fd = open(path, O_WRONLY);
	ssize_t n;
	if (fd < 0)
		return -1;
	n = write(fd, text, strlen(text));
	close(fd);
	return n < 0 ? -1 : 0;
}

/* KEY=value from a uevent file */
static void uevent_value(const char *uevent, const char *key, char *out, size_t size)
{
	size_t klen = strlen(key);
	const char *p = uevent;
	out[0] = 0;
	while (p && *p) {
		const char *end = strchr(p, '\n');
		size_t len = end ? (size_t)(end - p) : strlen(p);
		if (len > klen && strncmp(p, key, klen) == 0 && p[klen] == '=') {
			len -= klen + 1;
			if (len >= size)
				len = size - 1;
			memcpy(out, p + klen + 1, len);
			out[len] = 0;
			return;
		}
		p = end ? end + 1 : NULL;
	}
}

/* The driver bound to a HID device ("" if none) */
static void driver_of(const char *id, char *out, size_t size)
{
	char path[PATH_MAX], target[PATH_MAX];
	ssize_t n;
	path_of(path, sizeof(path), "/sys/bus/hid/devices/%s/driver", id);
	n = readlink(path, target, sizeof(target) - 1);
	out[0] = 0;
	if (n > 0) {
		const char *base;
		target[n] = 0;
		base = strrchr(target, '/');
		snprintf(out, size, "%s", base ? base + 1 : target);
	}
}

/* The hidraw node of a HID device ("hidraw3"), or -1 */
static int hidraw_of(const char *id, char *out, size_t size)
{
	char path[PATH_MAX];
	DIR *d;
	struct dirent *e;
	int found = -1;
	path_of(path, sizeof(path), "/sys/bus/hid/devices/%s/hidraw", id);
	d = opendir(path);
	if (!d)
		return -1;
	while ((e = readdir(d)) != NULL) {
		if (strncmp(e->d_name, "hidraw", 6) == 0) {
			snprintf(out, size, "%s", e->d_name);
			found = 0;
			break;
		}
	}
	closedir(d);
	return found;
}

static int valid_id(const char *id)
{
	return id[0] && !strchr(id, '/') && strcmp(id, ".") && strcmp(id, "..");
}

static int cmd_list(void)
{
	char dir[PATH_MAX], path[PATH_MAX], target[PATH_MAX], uevent[4096];
	char hid_id[64], name[256], uniq[64], driver[64];
	DIR *d;
	struct dirent *e;
	path_of(dir, sizeof(dir), "/sys/class/hidraw");
	d = opendir(dir);
	if (!d)
		return 0;
	while ((e = readdir(d)) != NULL) {
		unsigned bus = 0, vid = 0, pid = 0;
		const char *id;
		ssize_t n;
		if (strncmp(e->d_name, "hidraw", 6) != 0)
			continue;
		path_of(path, sizeof(path), "/sys/class/hidraw/%s/device", e->d_name);
		n = readlink(path, target, sizeof(target) - 1);
		if (n <= 0)
			continue;
		target[n] = 0;
		id = strrchr(target, '/') ? strrchr(target, '/') + 1 : target;
		path_of(path, sizeof(path), "/sys/class/hidraw/%s/device/uevent", e->d_name);
		if (read_text(path, uevent, sizeof(uevent)) < 0)
			continue;
		uevent_value(uevent, "HID_ID", hid_id, sizeof(hid_id));
		uevent_value(uevent, "HID_NAME", name, sizeof(name));
		uevent_value(uevent, "HID_UNIQ", uniq, sizeof(uniq));
		driver_of(id, driver, sizeof(driver));
		if (sscanf(hid_id, "%x:%x:%x", &bus, &vid, &pid) != 3)
			continue;
		printf("%s\t%s\t%04x\t%04x\t%04x\t%s\t%s\t%s\n", id, e->d_name, bus, vid, pid, driver, name, uniq);
	}
	closedir(d);
	return 0;
}

/* --- Frames on stdin / stdout */
static int write_all(int fd, const void *data, size_t len)
{
	const uint8_t *p = data;
	while (len) {
		ssize_t n = write(fd, p, len);
		if (n < 0) {
			if (errno == EINTR)
				continue;
			return -1;
		}
		p += n;
		len -= (size_t)n;
	}
	return 0;
}

static int send_frame(char type, const void *data, size_t len)
{
	uint8_t head[3] = { (uint8_t)type, (uint8_t)(len & 0xFF), (uint8_t)(len >> 8) };
	if (write_all(STDOUT_FILENO, head, 3) < 0)
		return -1;
	return len ? write_all(STDOUT_FILENO, data, len) : 0;
}

static void send_error(const char *fmt, ...)
{
	char msg[512];
	va_list ap;
	va_start(ap, fmt);
	vsnprintf(msg, sizeof(msg), fmt, ap);
	va_end(ap);
	send_frame('E', msg, strlen(msg));
}

static char moved_id[256];              /* Device given back to hid_nintendo at exit */

static void restore_driver(void)
{
	char path[PATH_MAX];
	if (!moved_id[0])
		return;
	path_of(path, sizeof(path), "/sys/bus/hid/drivers/hid-generic/unbind");
	write_text(path, moved_id);
	path_of(path, sizeof(path), "/sys/bus/hid/drivers/nintendo/bind");
	write_text(path, moved_id);
	moved_id[0] = 0;
}

static void on_signal(int sig)
{
	(void)sig;
	restore_driver();
	_exit(0);
}

static void sleep_ms(int ms)
{
	struct timespec ts = { ms / 1000, (long)(ms % 1000) * 1000000L };
	nanosleep(&ts, NULL);
}

/* hid_nintendo sends its own commands and changes the report mode: move the device to
 * hid-generic (its hidraw node is made again, maybe with another number). */
static int take_from_nintendo(const char *id, char *node, size_t size)
{
	char path[PATH_MAX];
	int i;
	path_of(path, sizeof(path), "/sys/bus/hid/drivers/nintendo/unbind");
	if (write_text(path, id) < 0)
		return -1;
	snprintf(moved_id, sizeof(moved_id), "%s", id);
	path_of(path, sizeof(path), "/sys/bus/hid/drivers/hid-generic/bind");
	write_text(path, id);
	for (i = 0; i < 100; i++) {         /* Up to 2s for the new hidraw node */
		if (hidraw_of(id, node, size) == 0) {
			path_of(path, sizeof(path), "/dev/%s", node);
			if (access(path, R_OK | W_OK) == 0)
				return 0;
		}
		sleep_ms(20);
	}
	return -1;
}

static int open_node(const char *path)
{
	struct stat st;
	if (stat(path, &st) == 0 && S_ISSOCK(st.st_mode)) {
		struct sockaddr_un addr;
		int fd = socket(AF_UNIX, SOCK_SEQPACKET, 0);
		if (fd < 0)
			return -1;
		memset(&addr, 0, sizeof(addr));
		addr.sun_family = AF_UNIX;
		snprintf(addr.sun_path, sizeof(addr.sun_path), "%s", path);
		if (connect(fd, (struct sockaddr *)&addr, sizeof(addr)) < 0) {
			close(fd);
			return -1;
		}
		return fd;
	}
	return open(path, O_RDWR | O_CLOEXEC);
}

static int cmd_open(const char *id, int keep_driver)
{
	char node[64], driver[64], path[PATH_MAX];
	uint8_t in[3 + 0xFFFF], report[MAX_REPORT];
	size_t have = 0;
	int fd;

	if (!valid_id(id)) {
		send_error("Invalid device id: %s", id);
		return 1;
	}
	if (hidraw_of(id, node, sizeof(node)) < 0) {
		send_error("No hidraw node for %s (is the controller still connected?)", id);
		return 1;
	}
	driver_of(id, driver, sizeof(driver));
	if (!keep_driver && strcmp(driver, "nintendo") == 0) {
		signal(SIGTERM, on_signal);
		signal(SIGHUP, on_signal);
		signal(SIGINT, on_signal);
		signal(SIGPIPE, on_signal);
		if (take_from_nintendo(id, node, sizeof(node)) < 0) {
			restore_driver();
			send_error("Could not move %s from the hid_nintendo driver to hid-generic", id);
			return 1;
		}
	}
	path_of(path, sizeof(path), "/dev/%s", node);
	fd = open_node(path);
	if (fd < 0) {
		send_error("Can't open %s: %s", path, strerror(errno));
		restore_driver();
		return 1;
	}
	send_frame('O', node, strlen(node));

	for (;;) {
		struct pollfd p[2] = { { STDIN_FILENO, POLLIN, 0 }, { fd, POLLIN, 0 } };
		if (poll(p, 2, -1) < 0) {
			if (errno == EINTR)
				continue;
			break;
		}
		if (p[1].revents & (POLLIN | POLLERR | POLLHUP)) {
			ssize_t n = read(fd, report, sizeof(report));
			if (n <= 0) {
				send_error("The controller is gone (%s)", n < 0 ? strerror(errno) : "closed");
				break;
			}
			if (send_frame('I', report, (size_t)n) < 0)
				break;
		}
		if (p[0].revents & (POLLIN | POLLERR | POLLHUP)) {
			ssize_t n = read(STDIN_FILENO, in + have, sizeof(in) - have);
			if (n <= 0)
				break;                  /* The app is gone */
			have += (size_t)n;
			for (;;) {
				size_t len;
				if (have < 3)
					break;
				len = in[1] | ((size_t)in[2] << 8);
				if (have < 3 + len)
					break;
				if (in[0] == 'Q') {
					close(fd);
					restore_driver();
					return 0;
				}
				if (in[0] == 'W' && len > 0 && write(fd, in + 3, len) < 0)
					send_error("Write failed: %s", strerror(errno));
				memmove(in, in + 3 + len, have - 3 - len);
				have -= 3 + len;
			}
		}
	}
	close(fd);
	restore_driver();
	return 1;
}

int main(int argc, char **argv)
{
	const char *r = getenv("JCTOOL_HIDRAW_ROOT");
	snprintf(root, sizeof(root), "%s", r ? r : "");
	setvbuf(stdout, NULL, _IOFBF, 1 << 16);
	if (argc >= 2 && strcmp(argv[1], "list") == 0) {
		int res = cmd_list();
		fflush(stdout);
		return res;
	}
	if (argc >= 3 && strcmp(argv[1], "open") == 0) {
		setvbuf(stdout, NULL, _IONBF, 0);
		return cmd_open(argv[2], argc >= 4 && strcmp(argv[3], "--keep-driver") == 0);
	}
	fprintf(stderr, "usage: %s list | open <hid-device-id> [--keep-driver]\n", argv[0]);
	return 2;
}
