#pragma once
#include <cstdint>
#include <string>

// Writes an 8-bit RGB image (width * height * 3 bytes, row by row) as a PNG file.
bool write_png_rgb(const std::string &path, const uint8_t *rgb, int width, int height);
