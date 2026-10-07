#include "formats/animation_timing.hpp"

#include <algorithm>

namespace patchy::animation {
std::optional<std::uint32_t> parse_layer_name_delay_ms(std::string_view name) {
  while (!name.empty() && (name.back() == ' ' || name.back() == '\t')) name.remove_suffix(1);
  if (name.empty() || name.back() != 's') return std::nullopt;
  const auto separator = name.find_last_of(" \t");
  auto token = separator == std::string_view::npos ? name : name.substr(separator + 1);
  token.remove_suffix(1);
  const auto dot = token.find('.');
  if (dot != std::string_view::npos && token.find('.', dot + 1) != std::string_view::npos) return std::nullopt;
  const auto whole = token.substr(0, dot);
  const auto fraction = dot == std::string_view::npos ? std::string_view{} : token.substr(dot + 1);
  if (whole.empty() && fraction.empty()) return std::nullopt;
  for (char c : whole) if (c < '0' || c > '9') return std::nullopt;
  for (char c : fraction) if (c < '0' || c > '9') return std::nullopt;
  std::uint64_t value = 0;
  for (char c : whole) value = std::min<std::uint64_t>(value * 10 + (c - '0'), kMaxFrameDelayMs);
  value *= 1000;
  for (std::size_t i = 0, factor = 100; i < 3; ++i, factor /= 10) {
    if (i < fraction.size()) value += static_cast<std::uint64_t>(fraction[i] - '0') * factor;
  }
  if (fraction.size() > 3 && fraction[3] >= '5') ++value;
  return static_cast<std::uint32_t>(std::min<std::uint64_t>(value, kMaxFrameDelayMs));
}

std::string format_delay_seconds_token(std::uint32_t milliseconds) {
  auto result = std::to_string(milliseconds / 1000);
  if (const auto remainder = milliseconds % 1000; remainder != 0) {
    auto fraction = std::to_string(1000 + remainder).substr(1);
    while (fraction.back() == '0') fraction.pop_back();
    result += "." + fraction;
  }
  return result + "s";
}
}  // namespace patchy::animation
