#include "classic-worker-custody.h"

#include <cstdlib>
#include <iostream>
#include <utility>

namespace {

using Ledger = braid::probe::ClassicWorkerCustody;
using Kind = Ledger::Kind;
using Refusal = Ledger::Refusal;
using Script = Ledger::ScriptType;
using Unsupported = Ledger::Unsupported;

#define EXPECT(condition)                            \
  do {                                               \
    if (!(condition)) {                              \
      std::cerr << __LINE__ << ": " #condition "\n"; \
      std::abort();                                  \
    }                                                \
  } while (false)

struct Fixture {
  Ledger ledger;
  const Ledger::Process process = ledger.BeginProcess(1, 2).value();
  const Ledger::Original document{process, Kind::kDocument, {1, 2}, 3};

  Fixture() {
    ledger.Enroll();
    ledger.DocumentLive(document);
  }

  Ledger::Lease Reserve(Ledger::Original parent, Ledger::Token worker) {
    auto factory = ledger.BindFactory(process, parent);
    EXPECT(factory);
    auto lease = ledger.Reserve(factory, process, worker, Script::kClassic);
    EXPECT(lease);
    EXPECT(lease->parent == parent);
    return *lease;
  }

  void Start(const Ledger::Lease& lease) {
    EXPECT(ledger.BindBlinkScope(process, lease, lease.parent,
                                 lease.worker.token, Script::kClassic));
    EXPECT(ledger.BlinkEvaluated(process, lease, Script::kClassic, true));
  }

  void Retire(const Ledger::Lease& lease) {
    EXPECT(ledger.BlinkSealedIngress(process, lease));
    EXPECT(ledger.BlinkRetired(process, lease, true));
    EXPECT(ledger.DrainOriginalEndpoint(process, lease));
  }
};

void OriginalDocumentAndNestedWorker() {
  Fixture f;
  auto worker = f.Reserve(f.document, {2, 1});
  EXPECT(f.ledger.BindBlinkScope(f.process, worker, f.document,
                                 worker.worker.token, Script::kClassic));
  // A worker can create a child during its top-level script.
  auto nested = f.Reserve(worker.worker, {2, 2});
  EXPECT(nested.worker.generation != worker.worker.generation);
  EXPECT(f.ledger.BlinkEvaluated(f.process, worker, Script::kClassic, true));
  f.Start(nested);
  f.ledger.Prepare();
  f.ledger.DocumentGone(f.document);
  f.Retire(nested);
  f.Retire(worker);
  EXPECT(f.ledger.CheckTerminal() == Refusal::kNone);
}

void FactoryCannotFollowReplacement() {
  Fixture f;
  auto old_factory = f.ledger.BindFactory(f.process, f.document);
  EXPECT(old_factory);
  f.ledger.DocumentGone(f.document);
  auto replacement = f.document;
  ++replacement.generation;  // Even with the same document token.
  f.ledger.DocumentLive(replacement);
  EXPECT(!f.ledger.Reserve(old_factory, f.process, {2, 1}, Script::kClassic));
  EXPECT(f.ledger.refusal() == Refusal::kUnresolvedOriginal);
  auto worker = f.Reserve(replacement, {2, 2});
  f.Start(worker);
  f.Retire(worker);
  f.ledger.Prepare();
  EXPECT(f.ledger.CheckTerminal() == Refusal::kUnresolvedOriginal);
}

void UnboundFactoriesStayUnbound() {
  Fixture f;
  auto unseen = f.document;
  ++unseen.generation;
  auto factory = f.ledger.BindFactory(f.process, unseen);
  EXPECT(!factory);
  f.ledger.DocumentLive(unseen);
  EXPECT(!f.ledger.Reserve(factory, f.process, {2, 1}, Script::kClassic));
  EXPECT(f.ledger.refusal() == Refusal::kUnresolvedOriginal);
}

void OriginalScopeAndProcessAreChecked() {
  {
    Fixture f;
    auto worker = f.Reserve(f.document, {2, 1});
    auto wrong_parent = f.document;
    ++wrong_parent.generation;
    EXPECT(!f.ledger.BindBlinkScope(f.process, worker, wrong_parent,
                                    worker.worker.token, Script::kClassic));
    EXPECT(f.ledger.refusal() == Refusal::kInvalidEvidence);
  }
  {
    Fixture f;
    auto worker = f.Reserve(f.document, {2, 1});
    auto foreign_process = f.ledger.BeginProcess(2, 3).value();
    EXPECT(!f.ledger.BindBlinkScope(foreign_process, worker, f.document,
                                    worker.worker.token, Script::kClassic));
    EXPECT(f.ledger.refusal() == Refusal::kInvalidEvidence);
  }
  {
    Fixture f;
    auto worker = f.Reserve(f.document, {2, 1});
    EXPECT(!f.ledger.BindBlinkScope(f.process, worker, f.document, {2, 2},
                                    Script::kClassic));
    EXPECT(f.ledger.refusal() == Refusal::kInvalidEvidence);
  }
}

void ProcessLossSurvivesHostReuseAndRejectsStaleEvidence() {
  Fixture f;
  auto worker = f.Reserve(f.document, {2, 1});
  f.Start(worker);
  f.ledger.ProcessLost(f.process);
  auto replacement =
      f.ledger.BeginProcess(f.process.context, f.process.host).value();
  EXPECT(replacement.incarnation != f.process.incarnation);
  EXPECT(!f.ledger.BlinkSealedIngress(f.process, worker));
  EXPECT(!f.ledger.BlinkSealedIngress(replacement, worker));
  f.ledger.Prepare();
  EXPECT(f.ledger.CheckTerminal() == Refusal::kProcessLost);
}

void TypeMustAgreeAtLaunchBindingAndEvaluation() {
  for (auto [type, refusal] :
       {std::pair{Script::kModule, Refusal::kModuleWorker},
        {Script::kUnknown, Refusal::kMissingScriptType}}) {
    Fixture f;
    auto factory = f.ledger.BindFactory(f.process, f.document);
    EXPECT(!f.ledger.Reserve(factory, f.process, {2, 1}, type));
    EXPECT(f.ledger.refusal() == refusal);
  }
  {
    Fixture f;
    auto worker = f.Reserve(f.document, {2, 1});
    EXPECT(!f.ledger.BindBlinkScope(f.process, worker, f.document,
                                    worker.worker.token, Script::kModule));
    EXPECT(f.ledger.refusal() == Refusal::kInvalidEvidence);
  }
  for (auto [type, success] :
       {std::pair{Script::kModule, true}, {Script::kClassic, false}}) {
    Fixture f;
    auto worker = f.Reserve(f.document, {2, 1});
    EXPECT(f.ledger.BindBlinkScope(f.process, worker, f.document,
                                   worker.worker.token, Script::kClassic));
    EXPECT(!f.ledger.BlinkEvaluated(f.process, worker, type, success));
    EXPECT(f.ledger.refusal() == Refusal::kStartUnresolved);
  }
}

void DuplicatesAndLateIngressRefuse() {
  {
    Fixture f;
    auto factory = f.ledger.BindFactory(f.process, f.document);
    EXPECT(factory);
    EXPECT(f.ledger.Reserve(factory, f.process, {2, 1}, Script::kClassic));
    EXPECT(!f.ledger.Reserve(factory, f.process, {2, 2}, Script::kClassic));
    EXPECT(f.ledger.refusal() == Refusal::kDuplicateLaunch);
  }
  {
    Fixture f;
    auto original = f.Reserve(f.document, {2, 1});
    f.Start(original);
    f.Retire(original);
    auto factory = f.ledger.BindFactory(f.process, f.document);
    EXPECT(!f.ledger.Reserve(factory, f.process, {2, 1}, Script::kClassic));
    EXPECT(f.ledger.refusal() == Refusal::kDuplicateLaunch);
  }
  {
    Fixture f;
    auto worker = f.Reserve(f.document, {2, 1});
    f.Start(worker);
    EXPECT(!f.ledger.BindBlinkScope(f.process, worker, f.document,
                                    worker.worker.token, Script::kClassic));
    EXPECT(f.ledger.refusal() == Refusal::kInvalidEvidence);
  }
  {
    Fixture f;
    auto factory = f.ledger.BindFactory(f.process, f.document);
    f.ledger.Prepare();
    EXPECT(!f.ledger.Reserve(factory, f.process, {2, 1}, Script::kClassic));
    EXPECT(f.ledger.CheckTerminal() == Refusal::kLateIngress);
  }
}

void AHostClosingIsNotRetirementAndNeitherIsOnlyABlinkMarker() {
  for (bool send_blink_retire : {false, true}) {
    Fixture f;
    auto original = f.Reserve(f.document, {2, 1});
    f.Start(original);
    auto replacement = f.Reserve(f.document, {2, 2});
    f.Start(replacement);
    f.ledger.Prepare();
    // Browser host deletion and DevTools detach have no settlement method.
    f.Retire(replacement);
    if (send_blink_retire) {
      EXPECT(f.ledger.BlinkSealedIngress(f.process, original));
      EXPECT(f.ledger.BlinkRetired(f.process, original, true));
    }
    EXPECT(f.ledger.CheckTerminal() == Refusal::kRetirementMissing);
    if (send_blink_retire) {
      EXPECT(f.ledger.DrainOriginalEndpoint(f.process, original));
      EXPECT(f.ledger.CheckTerminal() == Refusal::kRetirementMissing);
    }
  }
  {
    Fixture f;
    auto worker = f.Reserve(f.document, {2, 1});
    f.Start(worker);
    EXPECT(!f.ledger.DrainOriginalEndpoint(f.process, worker));
    EXPECT(f.ledger.refusal() == Refusal::kInvalidEvidence);
  }
}

void LostOrReplayedDrainCannotQualify() {
  {
    Fixture f;
    auto worker = f.Reserve(f.document, {2, 1});
    f.Start(worker);
    f.ledger.Prepare();
    EXPECT(f.ledger.BlinkSealedIngress(f.process, worker));
    EXPECT(f.ledger.BlinkRetired(f.process, worker, true));
    f.ledger.ProcessLost(f.process);
    EXPECT(!f.ledger.DrainOriginalEndpoint(f.process, worker));
    EXPECT(f.ledger.CheckTerminal() == Refusal::kProcessLost);
  }
  {
    Fixture f;
    auto worker = f.Reserve(f.document, {2, 1});
    f.Start(worker);
    f.ledger.Prepare();
    f.Retire(worker);
    EXPECT(!f.ledger.DrainOriginalEndpoint(f.process, worker));
    EXPECT(f.ledger.CheckTerminal() == Refusal::kInvalidEvidence);
  }
}

void AbandonedLaunchAndForcedDisposalRefuse() {
  {
    Fixture f;
    f.Reserve(f.document, {2, 1});
    f.ledger.Prepare();
    EXPECT(f.ledger.CheckTerminal() == Refusal::kStartUnresolved);
  }
  {
    Fixture f;
    f.Reserve(f.document, {2, 1});
    f.ledger.StartLost();
    EXPECT(f.ledger.refusal() == Refusal::kStartUnresolved);
  }
  {
    Fixture f;
    auto worker = f.Reserve(f.document, {2, 1});
    f.Start(worker);
    EXPECT(f.ledger.BlinkSealedIngress(f.process, worker));
    EXPECT(!f.ledger.BlinkRetired(f.process, worker, false));
    f.ledger.Prepare();
    EXPECT(f.ledger.CheckTerminal() == Refusal::kForcedOrUndrained);
  }
}

void UnsupportedIngressIsSticky() {
  for (auto [producer, refusal] :
       {std::pair{Unsupported::kShared, Refusal::kSharedWorker},
        {Unsupported::kService, Refusal::kServiceWorker},
        {Unsupported::kWorklet, Refusal::kWorklet}}) {
    // Check both pre-enrollment and active observation.
    for (bool before_enrollment : {false, true}) {
      Ledger ledger;
      if (!before_enrollment) {
        ledger.Enroll();
      }
      ledger.ObserveUnsupported(producer);
      if (before_enrollment) {
        ledger.Enroll();
      }
      ledger.Prepare();
      EXPECT(ledger.CheckTerminal() == refusal);
    }
    Fixture f;
    auto worker = f.Reserve(f.document, {2, 1});
    f.Start(worker);
    f.ledger.Prepare();
    f.ledger.ObserveUnsupported(producer);
    f.Retire(worker);
    EXPECT(f.ledger.CheckTerminal() == refusal);
  }

  Ledger ledger;
  auto process = ledger.BeginProcess(1, 2).value();
  EXPECT(!ledger.Reserve({}, process, {2, 1}, Script::kClassic));
  ledger.Enroll();
  ledger.Prepare();
  EXPECT(ledger.CheckTerminal() == Refusal::kBeforeEnrollment);
}

void CapacityRefusesWithoutEvictingOriginals() {
  Fixture f;
  auto original = f.Reserve(f.document, {2, 1});
  f.Start(original);
  for (unsigned i = 0; i < 300; ++i) {
    if (!f.ledger.BindFactory(f.process, f.document)) {
      break;
    }
  }
  EXPECT(f.ledger.refusal() == Refusal::kInventoryLimit);
  f.Retire(original);
  f.ledger.Prepare();
  EXPECT(f.ledger.CheckTerminal() == Refusal::kInventoryLimit);
}

}  // namespace

int main() {
  OriginalDocumentAndNestedWorker();
  FactoryCannotFollowReplacement();
  UnboundFactoriesStayUnbound();
  OriginalScopeAndProcessAreChecked();
  ProcessLossSurvivesHostReuseAndRejectsStaleEvidence();
  TypeMustAgreeAtLaunchBindingAndEvaluation();
  DuplicatesAndLateIngressRefuse();
  AHostClosingIsNotRetirementAndNeitherIsOnlyABlinkMarker();
  LostOrReplayedDrainCannotQualify();
  AbandonedLaunchAndForcedDisposalRefuse();
  UnsupportedIngressIsSticky();
  CapacityRefusesWithoutEvictingOriginals();
}
