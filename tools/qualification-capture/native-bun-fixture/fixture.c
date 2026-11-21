/* Test-only, Linux x86-64 SysV. Keep OS descriptors out of the FFI ABI. */
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <stdint.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <time.h>
#include <unistd.h>

_Static_assert(CHAR_BIT == 8 && sizeof(int) == 4, "Linux int/byte ABI");
_Static_assert(sizeof(uint32_t) == 4 && sizeof(int32_t) == 4, "FFI ABI");

enum {
    BAD_ARGUMENT = -1, BAD_DESCRIPTOR = -2, CANCELLED = -3,
    DEADLINE = -4, IO_FAILURE = -5, CLOSE_FAILURE = -6,
    MAX_FD = 67108863, CONTROL = 0, DIAGNOSTIC = 1
};

static int fits(int fd) {
    return fd >= 0 && fd <= MAX_FD;
}

static int local_stream(int fd) {
    struct sockaddr_un local = {0}, peer = {0};
    socklen_t ll = sizeof local, pl = sizeof peer;
    int type = 0;
    socklen_t tl = sizeof type;
    return fits(fd) &&
        getsockopt(fd, SOL_SOCKET, SO_TYPE, &type, &tl) == 0 &&
        tl == sizeof type && type == SOCK_STREAM &&
        getsockname(fd, (struct sockaddr *)&local, &ll) == 0 &&
        getpeername(fd, (struct sockaddr *)&peer, &pl) == 0 &&
        local.sun_family == AF_UNIX && peer.sun_family == AF_UNIX &&
        ll == sizeof local.sun_family && pl == sizeof peer.sun_family;
}

static int64_t now_ns(void) {
    struct timespec value;
    if (clock_gettime(CLOCK_MONOTONIC, &value) != 0)
        return -1;
    return (int64_t)value.tv_sec * 1000000000 + value.tv_nsec;
}

static int remaining_ms(int64_t end) {
    int64_t now = now_ns();
    if (now < 0)
        return IO_FAILURE;
    if (now >= end)
        return DEADLINE;
    return (int)((end - now + 999999) / 1000000);
}

static int settle(int result, const int *fds, size_t count) {
    int failed = 0;
    for (size_t i = 0; i < count; ++i) {
        /* Cleanup uses the original int even after a range refusal. Never retry close. */
        if (fds[i] >= 0 && close(fds[i]) != 0)
            failed = 1;
    }
    /* A cancelled result also asserts clean native settlement. */
    return failed ? CLOSE_FAILURE : result;
}

__attribute__((visibility("default")))
uint32_t braid_fixture_abi_v1(void) {
    return UINT32_C(0x42523401);
}

__attribute__((visibility("default")))
int32_t braid_fixture_query_v1(uint32_t mode, uint32_t word) {
    if (mode > 1 || word != 41)
        return BAD_ARGUMENT;
    if (!local_stream(CONTROL) || !local_stream(DIAGNOSTIC))
        return BAD_DESCRIPTOR;
    int64_t start = now_ns();
    if (start < 0)
        return IO_FAILURE;
    int64_t end = start + INT64_C(4000000000);

    int owned[3] = { -1, -1, -1 };
    owned[0] = fcntl(CONTROL, F_DUPFD_CLOEXEC, 4);
    if (!fits(owned[0]))
        return settle(BAD_DESCRIPTOR, owned, 3);
    /* The supervisor installed O_NONBLOCK on the shared control description. */
    int control_flags = fcntl(owned[0], F_GETFL);
    if (control_flags < 0 || !(control_flags & O_NONBLOCK))
        return settle(BAD_DESCRIPTOR, owned, 3);
    if (socketpair(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0, owned + 1) != 0)
        return settle(IO_FAILURE, owned, 3);
    if (!fits(owned[1]) || !fits(owned[2]))
        return settle(BAD_DESCRIPTOR, owned, 3);

    const unsigned char word_le[] = {41, 0, 0, 0};
    if (mode == 0) {
        if (send(owned[2], word_le, sizeof word_le, MSG_DONTWAIT | MSG_NOSIGNAL) !=
            (ssize_t)sizeof word_le)
            return settle(IO_FAILURE, owned, 3);
    } else if (send(DIAGNOSTIC, "W\n", 2, MSG_DONTWAIT | MSG_NOSIGNAL) != 2) {
        return settle(IO_FAILURE, owned, 3);
    }

    struct pollfd wait_for[] = {
        { .fd = owned[0], .events = POLLIN },
        { .fd = owned[1], .events = POLLIN }
    };
    unsigned char reply[4];
    size_t used = 0;
    int result;
    for (;;) {
        int ms = remaining_ms(end);
        if (ms < 0) {
            result = ms;
            break;
        }
        int count = poll(wait_for, 2, ms);
        if (count < 0) {
            if (errno == EINTR)
                continue;
            result = IO_FAILURE;
            break;
        }
        if (count == 0)
            continue;
        if ((ms = remaining_ms(end)) < 0) {
            result = ms;
            break;
        }
        if (wait_for[0].revents) {
            unsigned char command;
            ssize_t n = recv(owned[0], &command, 1, MSG_DONTWAIT);
            if (n < 0 && (errno == EINTR || errno == EAGAIN))
                continue;
            /* EOF is not a cooperative-cancel acknowledgment. */
            result = n == 1 && command == 'C' ? CANCELLED : IO_FAILURE;
            break;
        }
        if (!(wait_for[1].revents & POLLIN)) {
            result = IO_FAILURE;
            break;
        }
        ssize_t n = recv(owned[1], reply + used, sizeof reply - used, MSG_DONTWAIT);
        if (n < 0 && (errno == EINTR || errno == EAGAIN))
            continue;
        if (n <= 0) {
            result = IO_FAILURE;
            break;
        }
        used += (size_t)n;
        if (used == sizeof reply) {
            result = mode == 0 && reply[0] == 41 && reply[1] == 0 &&
                reply[2] == 0 && reply[3] == 0 ? 42 : IO_FAILURE;
            break;
        }
    }
    return settle(result, owned, 3);
}
