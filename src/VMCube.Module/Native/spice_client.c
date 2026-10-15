/* VMCUBE SPICE transport. Links dynamically to spice-client-glib (LGPL-2.1-or-later).
 * One process owns one GLib context, keeping callbacks and SPICE coroutines on one
 * thread, and isolating native codec failures from the Avalonia host.
 * stdout: VMSP + little-endian width,height,byte-count + opaque BGRA pixels.
 * stdin: password length + UTF-8 bytes, then 20-byte little-endian input packets.
 */
#include <spice-client.h>
#include <spice/enums.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#endif

static GMainLoop *loop;
static SpiceSession *session;
static SpiceDisplayChannel *display;
static SpiceInputsChannel *inputs;
static SpiceMainChannel *main_channel;
static gboolean dirty, received_frame;
static gint stopping;
static int exit_status;
static guint buttons;
static int last_x, last_y;
static gboolean have_position;

static guint32 read32(const guint8 *b) {
    return (guint32)b[0] | (guint32)b[1] << 8 | (guint32)b[2] << 16 | (guint32)b[3] << 24;
}
static void write32(guint8 *b, guint32 v) {
    for (int i = 0; i < 4; i++) b[i] = (guint8)(v >> (8 * i));
}
static void fail(const char *message) {
    if (!g_atomic_int_get(&stopping)) {
        fprintf(stderr, "%s\n", message); fflush(stderr);
        exit_status = 1; g_atomic_int_set(&stopping, 1);
        /* Disconnecting inside a channel coroutine can wait on its own callback.
         * This worker has no persistent state; the OS closes its sockets. */
        _Exit(1);
    }
}
static gboolean stop_from_input(gpointer unused) {
    g_atomic_int_set(&stopping, 1); _Exit(0); return G_SOURCE_REMOVE;
}
static gboolean startup_timeout(gpointer unused) {
    if (!received_frame) fail("SPICE 未在 25 秒内提供画面，请检查地址、端口、认证和图形设备配置。");
    return G_SOURCE_REMOVE;
}
static void channel_event(SpiceChannel *channel, SpiceChannelEvent event, gpointer unused) {
    if (g_atomic_int_get(&stopping)) return;
    switch (event) {
    case SPICE_CHANNEL_OPENED:
        if (SPICE_IS_MAIN_CHANNEL(channel)) spice_main_channel_request_mouse_mode(SPICE_MAIN_CHANNEL(channel), SPICE_MOUSE_MODE_CLIENT);
        break;
    case SPICE_CHANNEL_ERROR_AUTH: fail("SPICE 认证失败，请检查控制台密码。"); break;
    case SPICE_CHANNEL_ERROR_TLS: fail("SPICE TLS 校验失败，请检查证书、主机名和 CA 文件。"); break;
    case SPICE_CHANNEL_ERROR_CONNECT: fail("无法连接 SPICE 通道，请检查监听地址与端口。"); break;
    case SPICE_CHANNEL_ERROR_LINK: case SPICE_CHANNEL_ERROR_IO: fail("SPICE 通道连接已中断。"); break;
    case SPICE_CHANNEL_CLOSED:
        if (SPICE_IS_MAIN_CHANNEL(channel)) stop_from_input(NULL);
        break;
    default: break;
    }
}
static void invalidate(SpiceChannel *channel, gint x, gint y, gint width, gint height, gpointer unused) { dirty = TRUE; }
static void primary_create(SpiceChannel *channel, gint format, gint width, gint height, gint stride, gint shmid, gpointer data, gpointer unused) { dirty = TRUE; }
static void primary_destroy(SpiceChannel *channel, gpointer unused) { dirty = FALSE; }
static void mark(SpiceChannel *channel, gint value, gpointer unused) { dirty = TRUE; }
static void unsupported_gl(SpiceChannel *channel, guint x, guint y, guint width, guint height, gpointer unused) {
    spice_display_channel_gl_draw_done(SPICE_DISPLAY_CHANNEL(channel));
    fail("此 SPICE 控制台需要 GL scanout，请关闭虚拟机的 SPICE OpenGL 显示后连接。");
}
static void channel_new(SpiceSession *s, SpiceChannel *channel, gpointer unused) {
    int id = 0; g_object_get(channel, "channel-id", &id, NULL);
    if (SPICE_IS_MAIN_CHANNEL(channel)) {
        main_channel = SPICE_MAIN_CHANNEL(channel);

    } else if (SPICE_IS_DISPLAY_CHANNEL(channel) && id == 0) {
        display = SPICE_DISPLAY_CHANNEL(channel);
        g_signal_connect(channel, "display-primary-create", G_CALLBACK(primary_create), NULL);
        g_signal_connect(channel, "display-primary-destroy", G_CALLBACK(primary_destroy), NULL);
        g_signal_connect(channel, "display-invalidate", G_CALLBACK(invalidate), NULL);
        g_signal_connect(channel, "display-mark", G_CALLBACK(mark), NULL);
        g_signal_connect(channel, "gl-draw", G_CALLBACK(unsupported_gl), NULL);
        spice_channel_connect(channel);
    } else if (SPICE_IS_INPUTS_CHANNEL(channel)) {
        inputs = SPICE_INPUTS_CHANNEL(channel); spice_channel_connect(channel);
    }
    /* Cursor/audio/USB/file channels are deliberately not opened in this version. */
    if (SPICE_IS_MAIN_CHANNEL(channel) || SPICE_IS_DISPLAY_CHANNEL(channel) || SPICE_IS_INPUTS_CHANNEL(channel))
        g_signal_connect(channel, "channel-event", G_CALLBACK(channel_event), NULL);
}
static void channel_destroy(SpiceSession *s, SpiceChannel *channel, gpointer unused) {
    if ((gpointer)channel == display) { display = NULL; dirty = FALSE; }
    if ((gpointer)channel == inputs) inputs = NULL;
    if ((gpointer)channel == main_channel) main_channel = NULL;
}
static gboolean paint(gpointer unused) {
    if (g_atomic_int_get(&stopping)) return G_SOURCE_REMOVE;
    if (!display || !dirty) return G_SOURCE_CONTINUE;
    SpiceDisplayPrimary primary;
    if (!spice_display_channel_get_primary(SPICE_CHANNEL(display), 0, &primary)) return G_SOURCE_CONTINUE;
    if (primary.width < 1 || primary.height < 1 || primary.width > 8192 || primary.height > 8192 ||
        (guint64)primary.width * primary.height > 16 * 1024 * 1024 || !primary.data) {
        fail("SPICE 画面尺寸超出支持范围。"); return G_SOURCE_REMOVE;
    }
    const int bpp = (primary.format == SPICE_SURFACE_FMT_16_555 || primary.format == SPICE_SURFACE_FMT_16_565) ? 2 : 4;
    if ((primary.format != SPICE_SURFACE_FMT_32_xRGB && primary.format != SPICE_SURFACE_FMT_32_ARGB && bpp != 2) ||
        (gint64)ABS((gint64)primary.stride) < (gint64)primary.width * bpp) {
        fail("SPICE 返回不支持的像素格式或步长。"); return G_SOURCE_REMOVE;
    }
    guint32 size = (guint32)primary.width * (guint32)primary.height * 4;
    guint8 *pixels = g_malloc(size);
    for (int y = 0; y < primary.height; y++) {
        const guint8 *src = primary.data + (gssize)y * primary.stride;
        guint8 *dst = pixels + (gsize)y * primary.width * 4;
        for (int x = 0; x < primary.width; x++, dst += 4) {
            if (bpp == 4) { dst[0] = src[x*4]; dst[1] = src[x*4+1]; dst[2] = src[x*4+2]; }
            else {
                guint16 p; memcpy(&p, src + x*2, 2);
                guint r = primary.format == SPICE_SURFACE_FMT_16_565 ? (p >> 11) & 31 : (p >> 10) & 31;
                guint g = primary.format == SPICE_SURFACE_FMT_16_565 ? (p >> 5) & 63 : (p >> 5) & 31;
                guint maxg = primary.format == SPICE_SURFACE_FMT_16_565 ? 63 : 31;
                dst[0] = (guint8)((p & 31) * 255 / 31); dst[1] = (guint8)(g * 255 / maxg); dst[2] = (guint8)(r * 255 / 31);
            }
            dst[3] = 255;
        }
    }
    guint8 header[16] = {'V','M','S','P'};
    write32(header + 4, primary.width); write32(header + 8, primary.height); write32(header + 12, size);
    gboolean ok = fwrite(header, 1, 16, stdout) == 16 && fwrite(pixels, 1, size, stdout) == size && fflush(stdout) == 0;
    g_free(pixels);
    if (!ok) { stop_from_input(NULL); return G_SOURCE_REMOVE; }
    dirty = FALSE; received_frame = TRUE; return G_SOURCE_CONTINUE;
}
/* VNC's middle/right mask order differs from SPICE. */
static guint spice_buttons(guint mask) { return (mask & 1) | ((mask & 2) << 1) | ((mask & 4) >> 1); }
static gboolean input_packet(gpointer data) {
    guint8 *p = data;
    guint32 kind = read32(p), a = read32(p+4), b = read32(p+8), c = read32(p+12);
    gint32 wheel = (gint32)read32(p+16);
    if (!g_atomic_int_get(&stopping) && inputs) {
        if (kind == 1 && a <= 0x1ff) {
            if (b) spice_inputs_channel_key_press(inputs, a); else spice_inputs_channel_key_release(inputs, a);
        } else if (kind == 2) {
            guint state = spice_buttons(c & 7);
            int mode = SPICE_MOUSE_MODE_CLIENT;
            if (main_channel) g_object_get(main_channel, "mouse-mode", &mode, NULL);
            if (mode == SPICE_MOUSE_MODE_CLIENT) spice_inputs_channel_position(inputs, (int)a, (int)b, 0, state);
            else if (have_position) spice_inputs_channel_motion(inputs, (int)a-last_x, (int)b-last_y, state);
            last_x = (int)a; last_y = (int)b; have_position = TRUE;
            for (guint bit = 1, button = 1; bit <= 4; bit <<= 1, button++) {
                if ((buttons ^ state) & bit) {
                    if (state & bit) spice_inputs_channel_button_press(inputs, button, state);
                    else spice_inputs_channel_button_release(inputs, button, state);
                }
            }
            buttons = state;
            if (wheel) { guint button = wheel > 0 ? 4 : 5; spice_inputs_channel_button_press(inputs, button, state); spice_inputs_channel_button_release(inputs, button, state); }
        }
    }
    g_free(data); return G_SOURCE_REMOVE;
}
static gpointer read_inputs(gpointer unused) {
    guint8 packet[20];
    while (!g_atomic_int_get(&stopping) && fread(packet, 1, sizeof packet, stdin) == sizeof packet)
        g_main_context_invoke(NULL, input_packet, g_memdup2(packet, sizeof packet));
    g_main_context_invoke(NULL, stop_from_input, NULL); return NULL;
}
int main(int argc, char **argv) {
#ifdef _WIN32
    _setmode(_fileno(stdin), _O_BINARY); _setmode(_fileno(stdout), _O_BINARY);
#endif
    if (argc < 3 || argc > 5) { fprintf(stderr, "Usage: vmcube-spice HOST PORT [--tls [CA_FILE]]\n"); return 2; }
    char *end = NULL; long port = strtol(argv[2], &end, 10);
    if (!end || *end || port < 1 || port > 65535 || (argc >= 4 && strcmp(argv[3], "--tls"))) return 2;
    guint8 length[4]; if (fread(length, 1, 4, stdin) != 4) return 2;
    guint32 count = read32(length); if (count > 4096) return 2;
    gchar *password = g_malloc0(count + 1);
    if (fread(password, 1, count, stdin) != count || !g_utf8_validate(password, count, NULL)) { g_free(password); return 2; }
    loop = g_main_loop_new(NULL, FALSE); session = spice_session_new();
    g_object_set(session, "host", argv[1], argc >= 4 ? "tls-port" : "port", argv[2], "password", password,
        "enable-audio", FALSE, "enable-usbredir", FALSE, "enable-smartcard", FALSE, NULL);
    memset(password, 0, count); g_free(password);
    if (argc == 5) g_object_set(session, "ca-file", argv[4], NULL);
    g_signal_connect(session, "channel-new", G_CALLBACK(channel_new), NULL);
    g_signal_connect(session, "channel-destroy", G_CALLBACK(channel_destroy), NULL);
    guint timer = g_timeout_add(33, paint, NULL);
    guint deadline = g_timeout_add_seconds(25, startup_timeout, NULL);
    GThread *reader = g_thread_new("vmcube-spice-input", read_inputs, NULL); g_thread_unref(reader);
    if (!spice_session_connect(session)) fail("无法创建 SPICE 连接。");
    if (!g_atomic_int_get(&stopping)) g_main_loop_run(loop);
    g_atomic_int_set(&stopping, 1);
    GSource *source = g_main_context_find_source_by_id(NULL, timer); if (source) g_source_destroy(source);
    source = g_main_context_find_source_by_id(NULL, deadline); if (source) g_source_destroy(source);
    /* Do not reenter spice_session_disconnect from channel coroutine shutdown. */
    /* The OS reclaims the isolated session, pending callbacks and input reader. */
    return exit_status;
}
