// Minimal PNG writer (8-bit RGB, uncompressed deflate blocks), so jctool-cli can save
// IR camera images without extra libraries.

#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include "png.h"

static uint32_t crc_table[256];

static void make_crc_table()
{
    for (uint32_t n = 0; n < 256; n++) {
        uint32_t c = n;
        for (int k = 0; k < 8; k++)
            c = (c & 1) ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        crc_table[n] = c;
    }
}

static uint32_t crc32(const uint8_t *buf, size_t len, uint32_t crc = 0xFFFFFFFFu)
{
    if (crc_table[1] == 0)
        make_crc_table();
    for (size_t i = 0; i < len; i++)
        crc = crc_table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
    return crc;
}

static void put32(std::vector<uint8_t> &v, uint32_t x)
{
    v.push_back(x >> 24); v.push_back(x >> 16); v.push_back(x >> 8); v.push_back(x);
}

static void chunk(std::vector<uint8_t> &out, const char *type, const std::vector<uint8_t> &data)
{
    put32(out, (uint32_t)data.size());
    size_t start = out.size();
    out.insert(out.end(), type, type + 4);
    out.insert(out.end(), data.begin(), data.end());
    put32(out, crc32(&out[start], out.size() - start) ^ 0xFFFFFFFFu);
}

bool write_png_rgb(const std::string &path, const uint8_t *rgb, int width, int height)
{
    // Raw scanlines: filter byte 0 + RGB
    std::vector<uint8_t> raw;
    raw.reserve((size_t)(width * 3 + 1) * height);
    for (int y = 0; y < height; y++) {
        raw.push_back(0);
        raw.insert(raw.end(), rgb + (size_t)y * width * 3, rgb + (size_t)(y + 1) * width * 3);
    }

    // zlib stream with stored (uncompressed) deflate blocks
    std::vector<uint8_t> z;
    z.push_back(0x78); z.push_back(0x01);
    size_t pos = 0;
    do {
        size_t len = raw.size() - pos;
        if (len > 65535)
            len = 65535;
        bool last = pos + len == raw.size();
        z.push_back(last ? 1 : 0);
        z.push_back(len & 0xFF); z.push_back(len >> 8);
        z.push_back(~len & 0xFF); z.push_back((~len >> 8) & 0xFF);
        z.insert(z.end(), raw.begin() + pos, raw.begin() + pos + len);
        pos += len;
    } while (pos < raw.size());
    uint32_t a = 1, b = 0;
    for (uint8_t c : raw) {
        a = (a + c) % 65521;
        b = (b + a) % 65521;
    }
    put32(z, (b << 16) | a);

    std::vector<uint8_t> out = { 0x89, 'P', 'N', 'G', '\r', '\n', 0x1A, '\n' };
    std::vector<uint8_t> ihdr;
    put32(ihdr, width);
    put32(ihdr, height);
    ihdr.push_back(8);  // bit depth
    ihdr.push_back(2);  // color type: RGB
    ihdr.push_back(0); ihdr.push_back(0); ihdr.push_back(0);
    chunk(out, "IHDR", ihdr);
    chunk(out, "IDAT", z);
    chunk(out, "IEND", {});

    // Write to a temporary file and rename, so a viewer never sees a half-written image
    std::string tmp = path + ".tmp";
    FILE *f = fopen(tmp.c_str(), "wb");
    if (!f)
        return false;
    bool ok = fwrite(out.data(), 1, out.size(), f) == out.size();
    ok = fclose(f) == 0 && ok;
    return ok && rename(tmp.c_str(), path.c_str()) == 0;
}
