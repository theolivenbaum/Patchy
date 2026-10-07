#include "core/ink_space.hpp"

#include <algorithm>
#include <map>
#include <mutex>

namespace patchy {

namespace {

// Where `value` (0..255) falls on a grid of `nodes` samples: the lower node and the
// 16-bit fraction toward the next one. Integer math, so every toolchain agrees.
struct GridPosition {
  int lower{0};
  int upper{0};
  std::uint32_t fraction{0};  // 0..65535
};

GridPosition grid_position(std::uint8_t value, int nodes) noexcept {
  const auto scaled = static_cast<std::uint32_t>(value) * static_cast<std::uint32_t>(nodes - 1) * 65535U / 255U;
  const auto lower = static_cast<int>(scaled / 65535U);
  if (lower >= nodes - 1) {
    return {nodes - 1, nodes - 1, 0U};
  }
  return {lower, lower + 1, scaled % 65535U};
}

std::uint8_t byte_from_sample(std::uint64_t sample_times_weight, int weight_bits) noexcept {
  // sample is 0..65535 scaled by 2^weight_bits; reduce to 0..255 with rounding.
  const auto sample = (sample_times_weight + (std::uint64_t{1} << (weight_bits - 1))) >> weight_bits;
  return static_cast<std::uint8_t>(std::min<std::uint64_t>((sample * 255U + 32767U) / 65535U, 255U));
}

std::mutex& registry_mutex() {
  static std::mutex mutex;
  return mutex;
}

std::map<std::string, std::shared_ptr<const InkSpace>, std::less<>>& registry() {
  static std::map<std::string, std::shared_ptr<const InkSpace>, std::less<>> spaces;
  return spaces;
}

}  // namespace

bool InkSpace::valid() const noexcept {
  if (is_gray()) {
    return true;
  }
  if (rgb_grid < 2 || ink_grid < 2) {
    return false;
  }
  const auto rgb_nodes = static_cast<std::size_t>(rgb_grid) * static_cast<std::size_t>(rgb_grid) *
                         static_cast<std::size_t>(rgb_grid);
  const auto ink_nodes = static_cast<std::size_t>(ink_grid) * static_cast<std::size_t>(ink_grid) *
                         static_cast<std::size_t>(ink_grid) * static_cast<std::size_t>(ink_grid);
  return rgb_to_ink.size() == rgb_nodes * 4U && ink_to_rgb.size() == ink_nodes * 3U;
}

std::array<std::uint8_t, 4> InkSpace::ink_from_rgb(RgbColor color) const noexcept {
  const std::array<GridPosition, 3> axis{grid_position(color.red, rgb_grid), grid_position(color.green, rgb_grid),
                                         grid_position(color.blue, rgb_grid)};
  std::array<std::uint64_t, 4> sum{};
  for (int corner = 0; corner < 8; ++corner) {
    std::uint64_t weight = 1;
    std::size_t node = 0;
    for (int dimension = 0; dimension < 3; ++dimension) {
      const auto upper = ((corner >> dimension) & 1) != 0;
      const auto& position = axis[static_cast<std::size_t>(dimension)];
      weight *= upper ? position.fraction : 65535U - position.fraction;
      node = node * static_cast<std::size_t>(rgb_grid) +
             static_cast<std::size_t>(upper ? position.upper : position.lower);
    }
    if (weight == 0) {
      continue;
    }
    for (std::size_t channel = 0; channel < 4U; ++channel) {
      sum[channel] += weight * rgb_to_ink[node * 4U + channel];
    }
  }
  // Three weights of up to 65535 each: divide by 65535^3. 65535^3 is close enough to
  // 2^48 that the shift is exact to well under one 16-bit step.
  std::array<std::uint8_t, 4> ink{};
  for (std::size_t channel = 0; channel < 4U; ++channel) {
    ink[channel] = byte_from_sample(sum[channel], 48);
  }
  return ink;
}

RgbColor InkSpace::rgb_from_ink(const std::array<std::uint8_t, 4>& ink) const noexcept {
  const std::array<GridPosition, 4> axis{grid_position(ink[0], ink_grid), grid_position(ink[1], ink_grid),
                                         grid_position(ink[2], ink_grid), grid_position(ink[3], ink_grid)};
  // Four 16-bit weights would overflow 64 bits with the sample; halve each to 15 bits.
  std::array<std::uint64_t, 3> sum{};
  for (int corner = 0; corner < 16; ++corner) {
    std::uint64_t weight = 1;
    std::size_t node = 0;
    for (int dimension = 0; dimension < 4; ++dimension) {
      const auto upper = ((corner >> dimension) & 1) != 0;
      const auto& position = axis[static_cast<std::size_t>(dimension)];
      const auto fraction = position.fraction >> 1U;  // 0..32767
      weight *= upper ? fraction : 32767U - fraction;
      node = node * static_cast<std::size_t>(ink_grid) +
             static_cast<std::size_t>(upper ? position.upper : position.lower);
    }
    if (weight == 0) {
      continue;
    }
    for (std::size_t channel = 0; channel < 3U; ++channel) {
      sum[channel] += (weight >> 12U) * ink_to_rgb[node * 3U + channel];
    }
  }
  // Four 15-bit weights, less the 12 bits dropped above: 2^48 again.
  return RgbColor{byte_from_sample(sum[0], 48), byte_from_sample(sum[1], 48), byte_from_sample(sum[2], 48)};
}

void register_ink_space(std::shared_ptr<const InkSpace> space) {
  if (space == nullptr || space->id.empty() || !space->valid()) {
    return;
  }
  const std::lock_guard lock(registry_mutex());
  registry()[space->id] = std::move(space);
}

std::shared_ptr<const InkSpace> find_ink_space(std::string_view id) {
  if (id.empty()) {
    return nullptr;
  }
  const std::lock_guard lock(registry_mutex());
  const auto found = registry().find(id);
  return found != registry().end() ? found->second : nullptr;
}

}  // namespace patchy
