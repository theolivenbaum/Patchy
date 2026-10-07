# Private codec namespace

Based on the libwebp 1.6.0 release archive (SHA-256 in NOTICE-THIRD-PARTY.md).
Patchy's forced-include `../webp_symbols.hpp` prefixes globals, including platform
SIMD variants, so Qt can link its own copy. Generate it with
`scripts/dev/generate-webp-symbols.py` from the repository root.

Two upstream function-like macros in `src/utils/bit_reader_utils.h` expand to
`patchy_webp_VP8GetValue` and `patchy_webp_VP8GetSignedValue`. They discard a tracing
argument and rename a function in the same expansion, which a forced-include object
macro cannot override. These are the only source changes; BITTRACE remains off.
Codec algorithms are unchanged. Keep these namespace changes when upgrading and
audit the built archives' defined global symbols on desktop and wasm.
