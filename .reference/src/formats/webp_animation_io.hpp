#pragma once

#include "core/pixel_buffer.hpp"

#include <cstdint>
#include <functional>
#include <memory>
#include <span>
#include <vector>

namespace patchy::webp {
inline constexpr const char* kLoopCountMetadata = "webp.loop_count";
struct AnimationInfo {
  int width{};
  int height{};
  std::uint32_t frame_count{};
  std::uint16_t loop_count{};  // 0 forever; otherwise total plays
};
// Returns false for a still WebP. Recognized but damaged animations throw.
bool decode_animation(std::span<const std::uint8_t> bytes, std::uint64_t canvas_byte_limit,
                      const std::function<void(const AnimationInfo&)>& begin,
                      const std::function<void(PixelBuffer, std::uint32_t)>& frame);

class AnimationEncoder {
public:
  AnimationEncoder(int width, int height, std::uint16_t loops, int quality, bool lossless);
  ~AnimationEncoder();
  AnimationEncoder(const AnimationEncoder&) = delete;
  AnimationEncoder& operator=(const AnimationEncoder&) = delete;
  void add(const PixelBuffer& rgba, std::uint32_t duration_ms);
  [[nodiscard]] std::vector<std::uint8_t> finish();
private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
};
}  // namespace patchy::webp
