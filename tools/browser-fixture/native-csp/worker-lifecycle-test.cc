// Standalone test of the same header used by the Chromium worker patch.
#include <cstdlib>
#include <iostream>
#include <thread>

#include "third_party/blink/renderer/core/workers/csp_fixture_worker_lifecycle.h"

namespace {

using braid::probe::Scope;
using Ledger = braid::probe::WorkerCspLedger;
using Refusal = Ledger::Refusal;

#define EXPECT(condition)                            \
  do {                                               \
    if (!(condition)) {                              \
      std::cerr << __LINE__ << ": " #condition "\n"; \
      std::abort();                                  \
    }                                                \
  } while (false)

std::shared_ptr<Scope> Start(Ledger::Generation& generation) {
  auto scope = generation.Bind();
  EXPECT(scope);
  generation.TopLevelEvaluated(true);
  return scope;
}

void CrossThreadHandoffAndDispatch() {
  Ledger ledger;
  auto reservation = ledger.Reserve();
  EXPECT(reservation);
  std::thread worker([generation = std::move(reservation)]() {
    auto scope = Start(*generation);
    auto queued_dispatch = scope->Root();
    EXPECT(queued_dispatch);
    // Explicit settlement after confirmed dispatch returns.
    queued_dispatch->Completed();
    generation->BeginDisposal();
    generation->FinishDisposal(true);
  });
  worker.join();
  EXPECT(ledger.SealAndCheckTerminal() == Refusal::kNone);
}

void UnresolvedStartsCannotSettle() {
  {
    Ledger ledger;
    auto reservation = ledger.Reserve();
    EXPECT(reservation);
    // A cut while still waiting for Mojo, script load, or top-level execution
    // must refuse; later success or disposal cannot repair the attempt.
    EXPECT(ledger.SealAndCheckTerminal() == Refusal::kLiveProducer);
    Start(*reservation);
    reservation->BeginDisposal();
    reservation->FinishDisposal(true);
    EXPECT(ledger.SealAndCheckTerminal() == Refusal::kLiveProducer);
  }
  {
    Ledger ledger;
    EXPECT(
        ledger.Reserve());  // Drop before start callback, or factory failure.
    EXPECT(ledger.SealAndCheckTerminal() == Refusal::kUnresolvedStart);
  }
  {
    Ledger ledger;
    auto generation = ledger.Reserve();
    EXPECT(generation && generation->Bind());
    generation->TopLevelEvaluated(false);
    generation->BeginDisposal();
    generation->FinishDisposal(true);
    EXPECT(ledger.SealAndCheckTerminal() == Refusal::kUnresolvedStart);
  }
}

void ReplacementCannotSettleOriginal() {
  Ledger ledger;
  auto original = ledger.Reserve();
  auto replacement = ledger.Reserve();
  EXPECT(original && replacement && original->id() != replacement->id());
  auto original_scope = Start(*original);
  auto replacement_scope = Start(*replacement);
  auto original_dispatch = original_scope->Root();
  auto replacement_dispatch = replacement_scope->Root();
  EXPECT(original_dispatch && replacement_dispatch);
  replacement_dispatch->Completed();
  replacement->BeginDisposal();
  replacement->FinishDisposal(true);
  // Retiring the original with queued work refuses; another worker's
  // completion has not settled the original's dispatch.
  original->BeginDisposal();
  original->FinishDisposal(true);
  original_dispatch->Completed();
  EXPECT(ledger.SealAndCheckTerminal() == Refusal::kUnsettledDispatch);
}

void TeardownAndLateDispatchRefuse() {
  {
    Ledger ledger;
    auto generation = ledger.Reserve();
    auto scope = Start(*generation);
    EXPECT(scope->Root());  // Scheduler drops an undelivered closure.
    generation->BeginDisposal();
    generation->FinishDisposal(true);
    EXPECT(ledger.SealAndCheckTerminal() == Refusal::kUnsettledDispatch);
  }
  {
    Ledger ledger;
    auto generation = ledger.Reserve();
    Start(*generation);
    generation->BeginDisposal();
    generation->FinishDisposal(false);
    EXPECT(ledger.SealAndCheckTerminal() == Refusal::kForcedDisposal);
  }
  {
    Ledger ledger;
    auto generation = ledger.Reserve();
    auto retired_scope = Start(*generation);
    generation->BeginDisposal();
    generation->FinishDisposal(true);
    generation.reset();
    EXPECT(!retired_scope->Root());
    EXPECT(ledger.SealAndCheckTerminal() == Refusal::kUnsettledDispatch);
  }
  {
    Ledger ledger;
    auto generation = ledger.Reserve();
    Start(*generation);
    generation.reset();  // No disposal callback.
    EXPECT(ledger.SealAndCheckTerminal() == Refusal::kForcedDisposal);
  }
}

void UncoveredAndLateIngressRefuse() {
  for (auto reason :
       {Refusal::kUnsupportedWorklet, Refusal::kUnsupportedWorker,
        Refusal::kUnadmittedStart, Refusal::kOutsideSettingsDispatch}) {
    Ledger ledger;
    ledger.Refuse(reason);
    EXPECT(!ledger.Reserve());
    EXPECT(ledger.SealAndCheckTerminal() == reason);
  }
  {
    Ledger ledger;
    EXPECT(ledger.SealAndCheckTerminal() == Refusal::kNone);
    EXPECT(!ledger.Reserve());
    EXPECT(ledger.SealAndCheckTerminal() == Refusal::kLateIngress);
  }
  {
    Ledger ledger;
    unsigned admitted = 0;
    while (auto generation = ledger.Reserve()) {
      Start(*generation);
      generation->BeginDisposal();
      generation->FinishDisposal(true);
      ++admitted;
    }
    EXPECT(admitted == 256);
    EXPECT(ledger.SealAndCheckTerminal() == Refusal::kGenerationLimit);
  }
}

}  // namespace

int main() {
  CrossThreadHandoffAndDispatch();
  UnresolvedStartsCannotSettle();
  ReplacementCannotSettleOriginal();
  TeardownAndLateDispatchRefuse();
  UncoveredAndLateIngressRefuse();
  std::cout << "worker lifecycle checks passed (local accounting only)\n";
}
