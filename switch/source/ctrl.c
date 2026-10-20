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

// The SPI flash goes through hid's debug service: libnx's hiddbgRead/WriteSerialFlash,
// but waiting for each operation in steps, so that a controller that disconnects (or never
// answers) gives an error instead of a wait forever. When an operation doesn't finish, the
// system may still use its transfer memory, so that memory is left allocated.
#define OP_STEP_NS 250000000ULL          // Checks that the controller is still there
#define OP_TIMEOUT_NS 10000000000ULL

static bool still_connected(const Ctrl *c) {
    HidsysUniquePadId ids[16];
    s32 total = 0;
    if (R_FAILED(hidsysGetUniquePadIds(ids, 16, &total)))
        return true;                     // Can't tell: keep waiting until the timeout
    for (s32 i = 0; i < total && i < 16; i++)
        if (ids[i].id == c->id.id)
            return true;
    return false;
}

// cmd 229 (read) / 230 (write), after AcquireOperationEventHandle (228), as in libnx
static Result spi_op(const Ctrl *c, bool write, u32 offset, TransferMemory *tmem, u64 size, bool *abandoned) {
    Event ev = {0};
    Result rc = hiddbgAcquireOperationEventHandle(&ev, true, c->id);
    *abandoned = false;
    if (R_FAILED(rc))
        return rc;
    if (write) {
        const struct { u32 offset; u32 pad; u64 tmem_size; u64 size; HidsysUniquePadId id; } in = {
            offset, 0, tmem->size, size, c->id};
        rc = serviceDispatchIn(hiddbgGetServiceSession(), 230, in, .in_num_handles = 1, .in_handles = {tmem->handle});
    } else {
        const struct { u32 offset; u32 pad; u64 size; HidsysUniquePadId id; } in = {offset, 0, size, c->id};
        rc = serviceDispatchIn(hiddbgGetServiceSession(), 229, in, .in_num_handles = 1, .in_handles = {tmem->handle});
    }
    for (u64 waited = 0; R_SUCCEEDED(rc); waited += OP_STEP_NS) {
        Result w = eventWait(&ev, OP_STEP_NS);
        if (R_SUCCEEDED(w)) {
            rc = hiddbgGetOperationResult(c->id);
            break;
        }
        if (R_VALUE(w) != KERNELRESULT(TimedOut)) {
            rc = w;
            break;
        }
        if (!still_connected(c)) {
            rc = JC_ERR_DISCONNECTED;
            *abandoned = true;
        } else if (waited + OP_STEP_NS >= OP_TIMEOUT_NS) {
            rc = JC_ERR_TIMEOUT;
            *abandoned = true;
        }
    }
    eventClose(&ev);
    return rc;
}

static bool is_link_error(Result rc) {
    return rc == JC_ERR_TIMEOUT || rc == JC_ERR_DISCONNECTED;
}

// Reads go in pieces that keep each wait short. If the system turns a piece down, the
// rest go at the controller's own size (0x1D bytes per reply).
static size_t g_read_chunk = 0x200;

Result spi_read(const Ctrl *c, u32 offset, void *buf, size_t size) {
    if (hosversionBefore(6, 0, 0))
        return MAKERESULT(Module_Libnx, LibnxError_IncompatSysVer);
    u8 *p = buf;
    while (size) {
        size_t n = size < g_read_chunk ? size : g_read_chunk;
        TransferMemory tmem;
        bool abandoned;
        Result rc = tmemCreate(&tmem, (n + 0x1000) & ~0xFFF, Perm_Rw);
        if (R_FAILED(rc))
            return rc;
        rc = spi_op(c, false, offset, &tmem, n, &abandoned);
        if (R_SUCCEEDED(rc))
            memcpy(p, tmem.src_addr, n);
        if (abandoned)
            tmemCloseHandle(&tmem);      // Leaves the memory itself allocated
        else
            tmemClose(&tmem);
        if (R_FAILED(rc) && n > 0x1D && !is_link_error(rc)) {
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

Result spi_write(const Ctrl *c, u32 offset, const void *data, size_t size) {
    if (hosversionBefore(6, 0, 0))
        return MAKERESULT(Module_Libnx, LibnxError_IncompatSysVer);
    const u8 *p = data;
    while (size) {
        size_t n = size < WRITE_CHUNK ? size : WRITE_CHUNK;
        u8 *buf = aligned_alloc(0x1000, 0x1000);
        if (!buf)
            return MAKERESULT(Module_Libnx, LibnxError_OutOfMemory);
        memset(buf, 0, 0x1000);
        memcpy(buf, p, n);
        TransferMemory tmem;
        bool abandoned = false;
        Result rc = tmemCreateFromMemory(&tmem, buf, 0x1000, Perm_R);
        if (R_SUCCEEDED(rc)) {
            rc = spi_op(c, true, offset, &tmem, n, &abandoned);
            tmemCloseHandle(&tmem);
        }
        if (!abandoned)
            free(buf);
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
