#include "core/worker_budget.hpp"

#include "core/environment.hpp"

#include <algorithm>
#include <atomic>
#include <cstdlib>
#include <limits>
#include <thread>

#if defined(__EMSCRIPTEN_PTHREADS__)
#include <emscripten.h>
#include <emscripten/threading.h>
#endif

namespace patchy {

namespace {

// -1 = no scope active. Written by the main thread (BlockingFanoutBudgetScope),
// read by worker threads inside max_blocking_fanout_workers.
std::atomic<int> blocking_fanout_budget{-1};

}  // namespace

int idle_prespawned_pool_workers() {
#if defined(__EMSCRIPTEN_PTHREADS__)
  if (emscripten_is_main_browser_thread()) {
    return EM_ASM_INT({
      return (typeof PThread === 'undefined' || !PThread.unusedWorkers) ? 0 : PThread.unusedWorkers.length;
    });
  }
#endif
  return std::numeric_limits<int>::max();
}

int hardware_worker_threads() noexcept {
  static const int threads = [] {
    const int hardware = std::max(1, static_cast<int>(std::thread::hardware_concurrency()));
    const auto override_value = environment_variable("PATCHY_RENDER_THREADS");
    if (!override_value.has_value()) {
      return hardware;
    }
    char* end = nullptr;
    const long parsed = std::strtol(override_value->c_str(), &end, 10);
    if (end == override_value->c_str() || parsed < 1) {
      return hardware;
    }
    return static_cast<int>(std::min<long>(parsed, 256));
  }();
  return threads;
}

int max_blocking_fanout_workers(int wanted) {
#if defined(__EMSCRIPTEN_PTHREADS__)
  if (emscripten_is_main_browser_thread()) {
    // PThread.unusedWorkers holds the pre-spawned pool workers not currently
    // running a pthread. Two workers of margin absorb spawns racing in from
    // worker threads between this read and the fan-out's own pthread_create
    // calls (main-thread spawns cannot interleave; this thread does not
    // yield between the read and the creates).
    const int idle = idle_prespawned_pool_workers();
    return std::clamp(idle - 2, 0, wanted);
  }
  const int budget = blocking_fanout_budget.load(std::memory_order_relaxed);
  if (budget >= 0) {
    return std::min(wanted, budget);
  }
#endif
  return wanted;
}

// A negative budget constructs an inert scope (nothing published, destructor
// restores the unchanged value), so call sites can build one unconditionally.
BlockingFanoutBudgetScope::BlockingFanoutBudgetScope(int budget)
    : previous_budget_(budget < 0
                           ? blocking_fanout_budget.load(std::memory_order_relaxed)
                           : blocking_fanout_budget.exchange(budget, std::memory_order_relaxed)) {}

BlockingFanoutBudgetScope::~BlockingFanoutBudgetScope() {
  blocking_fanout_budget.store(previous_budget_, std::memory_order_relaxed);
}

}  // namespace patchy
