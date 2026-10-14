#include "common.h"

static const HidNpadIdType NPADS[] = {
    HidNpadIdType_No1, HidNpadIdType_No2, HidNpadIdType_No3, HidNpadIdType_No4, HidNpadIdType_No5,
    HidNpadIdType_No6, HidNpadIdType_No7, HidNpadIdType_No8, HidNpadIdType_Handheld,
};

static void find_npad(Ctrl *c) {
    c->has_npad = false;
    for (size_t i = 0; i < sizeof(NPADS) / sizeof(NPADS[0]); i++) {
        HidsysUniquePadId pads[2];
        s32 total = 0;
        if (R_FAILED(hidsysGetUniquePadsFromNpad(NPADS[i], pads, 2, &total)))
            continue;
        for (s32 j = 0; j < total && j < 2; j++) {
            if (pads[j].id == c->id.id) {
                c->has_npad = true;
                c->npad = NPADS[i];
                return;
            }
        }
    }
}

static bool fill(Ctrl *c, HidsysUniquePadId id) {
    HidsysUniquePadType pad_type;
    memset(c, 0, sizeof(*c));
    c->id = id;
    if (R_FAILED(hidsysGetUniquePadType(id, &pad_type)))
        return false;
    switch (pad_type) {
        case HidsysUniquePadType_LeftController: c->type = TYPE_LEFT; break;
        case HidsysUniquePadType_RightController: c->type = TYPE_RIGHT; break;
        case HidsysUniquePadType_FullKeyController: c->type = TYPE_PRO; break;
        default: return false;        // The Switch Lite's own controls, debug pads
    }
    hidsysGetUniquePadInterface(id, &c->iface);
    BtdrvAddress addr;
    if (R_SUCCEEDED(hidsysGetUniquePadBluetoothAddress(id, &addr)))
        memcpy(c->mac, addr.address, 6);
    HidsysUniquePadSerialNumber sn;
    if (R_SUCCEEDED(hidsysGetUniquePadSerialNumber(id, &sn))) {
        memcpy(c->sn, sn.serial_number, 16);
        c->sn[16] = 0;
    }
    find_npad(c);
    return true;
}

int ctrl_list(Ctrl *out, int max) {
    HidsysUniquePadId ids[16];
    s32 total = 0;
    int n = 0;
    if (R_FAILED(hidsysGetUniquePadIds(ids, 16, &total)))
        return 0;
    for (s32 i = 0; i < total && i < 16 && n < max; i++) {
        if (fill(&out[n], ids[i]))
            n++;
    }
    return n;
}

bool ctrl_refresh(Ctrl *c) {
    HidsysUniquePadId ids[16];
    s32 total = 0;
    if (R_FAILED(hidsysGetUniquePadIds(ids, 16, &total)))
        return false;
    for (s32 i = 0; i < total && i < 16; i++) {
        if (ids[i].id == c->id.id) {
            find_npad(c);
            return true;
        }
    }
    return false;
}

const char *ctrl_type_name(int type) {
    switch (type) {
        case TYPE_LEFT: return "Joy-Con (L)";
        case TYPE_RIGHT: return "Joy-Con (R)";
        case TYPE_PRO: return "Pro Controller";
    }
    return "Unknown";
}

const char *ctrl_iface_name(HidNpadInterfaceType iface) {
    switch ((int)iface) {
        case HidNpadInterfaceType_Bluetooth: return "Bluetooth";
        case HidNpadInterfaceType_Rail: return "Attached (rail)";
        case HidNpadInterfaceType_USB: return "USB";
    }
    return "Other";
}

void ctrl_mac_text(const Ctrl *c, char *out) {
    sprintf(out, "%02X:%02X:%02X:%02X:%02X:%02X", c->mac[0], c->mac[1], c->mac[2], c->mac[3], c->mac[4], c->mac[5]);
}

// Reads go in pieces that keep each wait short. If the system turns a piece down, the
// rest go at the controller's own size (0x1D bytes per reply).
static size_t g_read_chunk = 0x200;

Result spi_read(const Ctrl *c, u32 offset, void *buf, size_t size) {
    u8 *p = buf;
    while (size) {
        size_t n = size < g_read_chunk ? size : g_read_chunk;
        Result rc = hiddbgReadSerialFlash(offset, p, n, c->id);
        if (R_FAILED(rc) && n > 0x1D) {
            g_read_chunk = 0x1D;
            continue;
        }
        if (R_FAILED(rc))
            return rc;
        offset += n;
        p += n;
        size -= n;
    }
    return 0;
}

// Writes go 0x10 bytes at a time, like the desktop app's full restore: one write
// subcommand each.
#define WRITE_CHUNK 0x10
static u8 g_write_buf[0x1000] __attribute__((aligned(0x1000)));

Result spi_write(const Ctrl *c, u32 offset, const void *data, size_t size) {
    const u8 *p = data;
    while (size) {
        size_t n = size < WRITE_CHUNK ? size : WRITE_CHUNK;
        memset(g_write_buf, 0, sizeof(g_write_buf));
        memcpy(g_write_buf, p, n);
        Result rc = hiddbgWriteSerialFlash(offset, g_write_buf, sizeof(g_write_buf), n, c->id);
        if (R_FAILED(rc))
            return rc;
        offset += n;
        p += n;
        size -= n;
    }
    return 0;
}

u32 ctrl_style(const Ctrl *c) {
    if (!c->has_npad)
        return 0;
    u32 set = hidGetNpadStyleSet(c->npad);
    static const u32 STYLES[] = {HidNpadStyleTag_NpadFullKey, HidNpadStyleTag_NpadHandheld, HidNpadStyleTag_NpadJoyDual,
                                 HidNpadStyleTag_NpadJoyLeft, HidNpadStyleTag_NpadJoyRight};
    for (size_t i = 0; i < sizeof(STYLES) / sizeof(STYLES[0]); i++)
        if (set & STYLES[i])
            return STYLES[i];
    return 0;
}

bool ctrl_battery(const Ctrl *c, HidPowerInfo *info) {
    u32 style = ctrl_style(c);
    if (!style)
        return false;
    if (style == HidNpadStyleTag_NpadJoyDual || style == HidNpadStyleTag_NpadHandheld) {
        HidPowerInfo left, right;
        hidGetNpadPowerInfoSplit(c->npad, &left, &right);
        *info = c->type == TYPE_LEFT ? left : right;
    } else {
        hidGetNpadPowerInfoSingle(c->npad, info);
    }
    return true;
}
