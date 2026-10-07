#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

namespace patchy::animation {
inline constexpr std::uint32_t kMaxFrameDelayMs = 0xffffff;
[[nodiscard]] std::optional<std::uint32_t> parse_layer_name_delay_ms(std::string_view name);
[[nodiscard]] std::string format_delay_seconds_token(std::uint32_t milliseconds);
}  // namespace patchy::animation
