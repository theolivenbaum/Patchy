#include "formats/webp_symbols.hpp"
#include "formats/webp_animation_io.hpp"
#include "formats/animation_timing.hpp"
#include "support/translate_noop.hpp"

#include "webp/decode.h"
#include "webp/demux.h"
#include "webp/encode.h"
#include "webp/mux.h"

#include <cstring>
#include <limits>
#include <stdexcept>

namespace patchy::webp {
namespace {
[[noreturn]] void invalid_animation() {
  throw std::runtime_error(PATCHY_TRANSLATE_NOOP("QObject", "Invalid or damaged animated WebP."));
}
using Demux = std::unique_ptr<WebPDemuxer, decltype(&WebPDemuxDelete)>;
using Decoder = std::unique_ptr<WebPAnimDecoder, decltype(&WebPAnimDecoderDelete)>;
}

bool decode_animation(std::span<const std::uint8_t> bytes, std::uint64_t canvas_byte_limit,
                      const std::function<void(const AnimationInfo&)>& begin,
                      const std::function<void(PixelBuffer, std::uint32_t)>& frame) {
  WebPBitstreamFeatures features{};
  if (WebPGetFeatures(bytes.data(), bytes.size(), &features) != VP8_STATUS_OK) invalid_animation();
  if (!features.has_animation) return false;
  const WebPData data{bytes.data(), bytes.size()};
  Demux demux(WebPDemux(&data), &WebPDemuxDelete);
  if (!demux) invalid_animation();
  AnimationInfo info{static_cast<int>(WebPDemuxGetI(demux.get(), WEBP_FF_CANVAS_WIDTH)),
                     static_cast<int>(WebPDemuxGetI(demux.get(), WEBP_FF_CANVAS_HEIGHT)),
                     WebPDemuxGetI(demux.get(), WEBP_FF_FRAME_COUNT),
                     static_cast<std::uint16_t>(WebPDemuxGetI(demux.get(), WEBP_FF_LOOP_COUNT))};
  const auto canvas_bytes = static_cast<std::uint64_t>(info.width) * static_cast<std::uint64_t>(info.height) * 4;
  if (info.width <= 0 || info.height <= 0 || info.frame_count == 0 ||
      canvas_bytes > std::numeric_limits<std::size_t>::max() ||
      info.frame_count > std::numeric_limits<std::size_t>::max() / canvas_bytes ||
      (canvas_byte_limit != 0 && canvas_bytes > canvas_byte_limit)) {
    throw std::runtime_error(PATCHY_TRANSLATE_NOOP("QObject", "Animated WebP exceeds the image allocation limit."));
  }
  // Preflight duration arithmetic before the decoder accumulates timestamps in int.
  WebPIterator iter{};
  if (!WebPDemuxGetFrame(demux.get(), 1, &iter)) invalid_animation();
  std::uint64_t total_ms = 0;
  bool complete = true;
  do {
    total_ms += static_cast<unsigned>(iter.duration);
    complete = complete && iter.complete;
  } while (WebPDemuxNextFrame(&iter));
  WebPDemuxReleaseIterator(&iter);
  if (!complete || total_ms > static_cast<std::uint64_t>(std::numeric_limits<int>::max())) invalid_animation();
  WebPAnimDecoderOptions options{};
  if (!WebPAnimDecoderOptionsInit(&options)) invalid_animation();
  options.color_mode = MODE_RGBA;
  Decoder decoder(WebPAnimDecoderNew(&data, &options), &WebPAnimDecoderDelete);
  if (!decoder) invalid_animation();
  begin(info);
  int previous = 0;
  for (std::uint32_t i = 0; i < info.frame_count; ++i) {
    std::uint8_t* buffer = nullptr;
    int timestamp = 0;
    if (!WebPAnimDecoderGetNext(decoder.get(), &buffer, &timestamp) || timestamp < previous) invalid_animation();
    PixelBuffer pixels(info.width, info.height, PixelFormat::rgba8());
    std::memcpy(pixels.data().data(), buffer, static_cast<std::size_t>(canvas_bytes));
    const auto duration = static_cast<std::uint32_t>(timestamp - previous);
    previous = timestamp;
    frame(std::move(pixels), duration);
  }
  return true;
}

struct AnimationEncoder::Impl {
  std::unique_ptr<WebPAnimEncoder, decltype(&WebPAnimEncoderDelete)> encoder{nullptr, &WebPAnimEncoderDelete};
  WebPConfig config{};
  int width{};
  int height{};
  int timestamp{};
  bool has_frames{};
  bool finished{};
};

AnimationEncoder::AnimationEncoder(int width, int height, std::uint16_t loops, int quality, bool lossless)
    : impl_(std::make_unique<Impl>()) {
  if (width <= 0 || height <= 0 || width > WEBP_MAX_DIMENSION || height > WEBP_MAX_DIMENSION ||
      quality < 0 || quality > 100) {
    throw std::runtime_error(PATCHY_TRANSLATE_NOOP("QObject", "Invalid animated WebP dimensions or quality."));
  }
  WebPAnimEncoderOptions options{};
  if (!WebPAnimEncoderOptionsInit(&options) || !WebPConfigInit(&impl_->config)) invalid_animation();
  options.anim_params.loop_count = loops;
  options.anim_params.bgcolor = 0;
  options.allow_mixed = 0;
  impl_->encoder.reset(WebPAnimEncoderNew(width, height, &options));
  if (!impl_->encoder) throw std::runtime_error(PATCHY_TRANSLATE_NOOP("QObject", "Could not create animated WebP encoder."));
  impl_->width = width;
  impl_->height = height;
  impl_->config.lossless = lossless || quality == 100;
  impl_->config.quality = impl_->config.lossless ? 70.0f : static_cast<float>(quality);
  impl_->config.alpha_quality = 100;
  impl_->config.exact = 1;
}

AnimationEncoder::~AnimationEncoder() = default;

void AnimationEncoder::add(const PixelBuffer& rgba, std::uint32_t duration_ms) {
  if (impl_->finished || rgba.width() != impl_->width || rgba.height() != impl_->height ||
      rgba.format() != PixelFormat::rgba8() || duration_ms > animation::kMaxFrameDelayMs ||
      duration_ms > static_cast<std::uint32_t>(std::numeric_limits<int>::max() - impl_->timestamp)) {
    throw std::runtime_error(PATCHY_TRANSLATE_NOOP("QObject", "Invalid animated WebP frame or duration."));
  }
  WebPPicture picture{};
  if (!WebPPictureInit(&picture)) invalid_animation();
  const auto cleanup = std::unique_ptr<WebPPicture, decltype(&WebPPictureFree)>(&picture, &WebPPictureFree);
  picture.width = rgba.width();
  picture.height = rgba.height();
  picture.use_argb = 1;
  if (!WebPPictureImportRGBA(&picture, rgba.data().data(), rgba.width() * 4) ||
      !WebPAnimEncoderAdd(impl_->encoder.get(), &picture, impl_->timestamp, &impl_->config)) {
    throw std::runtime_error(PATCHY_TRANSLATE_NOOP("QObject", "Could not encode animated WebP frame."));
  }
  impl_->timestamp += static_cast<int>(duration_ms);
  impl_->has_frames = true;
}

std::vector<std::uint8_t> AnimationEncoder::finish() {
  if (impl_->finished || !impl_->has_frames) {
    throw std::runtime_error(PATCHY_TRANSLATE_NOOP("QObject", "Animated WebP needs at least one frame."));
  }
  impl_->finished = true;
  WebPData data{};
  const auto cleanup = std::unique_ptr<WebPData, decltype(&WebPDataClear)>(&data, &WebPDataClear);
  if (!WebPAnimEncoderAdd(impl_->encoder.get(), nullptr, impl_->timestamp, nullptr) ||
      !WebPAnimEncoderAssemble(impl_->encoder.get(), &data)) {
    throw std::runtime_error(PATCHY_TRANSLATE_NOOP("QObject", "Could not finish animated WebP."));
  }
  return {data.bytes, data.bytes + data.size};
}
}  // namespace patchy::webp
