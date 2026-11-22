// Standalone controls for the same lifecycle ledger used by the Blink patch.
#include <cstdlib>
#include <iostream>
#include <string_view>

#include "third_party/blink/renderer/core/frame/csp/fixture_lifecycle_ingress.h"

namespace {
using Ledger = braid::probe::LifecycleIngress;
using Failure = Ledger::Failure;
using Provenance = Ledger::Provenance;

#define EXPECT(condition)                            \
  do {                                               \
    if (!(condition)) {                              \
      std::cerr << __LINE__ << ": " #condition "\n"; \
      std::abort();                                  \
    }                                                \
  } while (false)

constexpr std::string_view original = "original-token";
constexpr std::string_view replacement = "replacement-token";

void NaturalRemovalAndDescendants() {
  Ledger book;
  int focus = 0, visibility = 0;
  EXPECT(book.BindOriginal(original));
  book.Register(&focus);
  book.Register(&visibility);
  EXPECT(!book.Ready());
  auto outer = book.Enter(&focus, Provenance::kTrustedDom);
  auto timer = book.Publish(original, outer);
  auto inner = book.Enter(&visibility, Provenance::kTrustedDom);
  auto request = book.Publish(original, inner);
  EXPECT(outer && inner && timer && request && timer != request);
  EXPECT(book.CurrentCallback() == inner);
  book.Remove(&visibility);  // Explicit removal during delivery is allowed.
  book.AcknowledgeCoverage(original, inner);
  book.Leave(inner);
  EXPECT(book.CurrentCallback() == outer);
  book.AcknowledgeCoverage(original, outer);
  book.Leave(outer);
  book.Remove(&focus);
  EXPECT(!book.Ready());
  auto csp = book.Publish(original, request);  // Callback already returned.
  EXPECT(csp);
  book.Complete(original, timer);
  book.Complete(original, request);
  EXPECT(!book.Ready());
  book.Complete(original, csp);
  EXPECT(book.trusted_calls() == 2 && book.synthetic_calls() == 0);
  EXPECT(book.Ready());
  auto ticket = book.Prepare(original);
  EXPECT(ticket.revision && !book.VerifyRetirement(ticket));
  book.Detach(original);
  EXPECT(book.VerifyRetirement(ticket));
  EXPECT(!book.VerifyRetirement({std::string(replacement), ticket.revision}));
  EXPECT(!book.VerifyRetirement({std::string(original), ticket.revision + 1}));
}

void RefusalDoesNotCancelAnAdmittedCallback() {
  Ledger book;
  int listener = 0;
  EXPECT(book.BindOriginal(original));
  book.Register(&listener);
  int invoked = 0;
  auto guarded_invoke = [&] {
    const auto credit = book.Enter(&listener, Provenance::kTrustedDom);
    ++invoked;  // Blink must still call through even if credit was refused.
    if (credit) {
      book.AcknowledgeCoverage(original, credit);
    }
    book.Leave(credit);
  };
  guarded_invoke();
  EXPECT(!book.Prepare(original).revision);
  guarded_invoke();
  EXPECT(invoked == 2 && book.failure() == Failure::kPendingAtPrepare);
  book.Remove(&listener);
  book.Detach(original);
  EXPECT(!book.VerifyRetirement({std::string(original), 1}));
}

void UnknownsAndDroppedWorkRefuse() {
  int listener = 0;
  {
    Ledger book;
    EXPECT(book.BindOriginal(original));
    book.Register(&listener);
    // Trust and settlement are distinct: a synthetic callback may still
    // execute real script and needs descendant coverage as well.
    auto credit = book.Enter(&listener, Provenance::kSynthetic);
    EXPECT(credit && book.trusted_calls() == 0 && book.synthetic_calls() == 1);
    book.Leave(credit);
    book.Remove(&listener);
    EXPECT(book.failure() == Failure::kUnsupportedPublication);
    EXPECT(!book.Prepare(original).revision);
  }
  {
    Ledger book;
    EXPECT(book.BindOriginal(original));
    book.Register(&listener);
    book.BulkClear(&listener);
    EXPECT(book.failure() == Failure::kLostAtClear);
  }
  {
    Ledger book;
    EXPECT(book.BindOriginal(original));
    book.Register(&listener);
    auto credit = book.Enter(&listener, Provenance::kTrustedDom);
    book.Remove(&listener);  // Also models the 'once' order in Blink.
    EXPECT(!book.Ready());   // Removing a running listener is not settlement.
    book.AcknowledgeCoverage(original, credit);
    book.Leave(credit);
    EXPECT(book.Ready());
    auto ticket = book.Prepare(original);
    book.Register(&listener);  // It still reaches Blink; invalidate the cut.
    book.Detach(original);
    EXPECT(book.failure() == Failure::kLatePublication);
    EXPECT(!book.VerifyRetirement(ticket));
  }
  {
    Ledger book;
    EXPECT(book.BindOriginal(original));
    book.Register(&listener);
    auto credit = book.Enter(&listener, Provenance::kTrustedDom);
    auto child = book.Publish(original, credit);
    book.AcknowledgeCoverage(original, credit);
    book.Leave(credit);
    book.Remove(&listener);
    book.Complete(replacement, child);
    EXPECT(book.failure() == Failure::kWrongOriginal);
    EXPECT(!book.Prepare(original).revision);
  }
  {
    Ledger book;
    EXPECT(book.BindOriginal(original));
    book.Register(&listener);
    auto credit = book.Enter(&listener, Provenance::kTrustedDom);
    auto child = book.Publish(original, credit);
    book.AcknowledgeCoverage(original, credit);
    book.Leave(credit);
    book.Remove(&listener);
    EXPECT(!book.Prepare(original).revision);
    book.Complete(original, child);
    book.Detach(original);
    EXPECT(book.failure() == Failure::kPendingAtPrepare);
  }
  {
    Ledger book;
    EXPECT(book.BindOriginal(original));
    auto ticket = book.Prepare(original);
    book.Refuse(Failure::kWindowReuse);
    book.Detach(replacement);
    EXPECT(!book.VerifyRetirement(ticket));
  }
}
}  // namespace

int main() {
  NaturalRemovalAndDescendants();
  RefusalDoesNotCancelAnAdmittedCallback();
  UnknownsAndDroppedWorkRefuse();
  std::cout << "lifecycle ingress checks passed (local model only)\n";
}
