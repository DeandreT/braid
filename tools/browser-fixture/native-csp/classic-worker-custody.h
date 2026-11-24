#ifndef BRAID_FIXTURE_CLASSIC_WORKER_CUSTODY_H_
#define BRAID_FIXTURE_CLASSIC_WORKER_CUSTODY_H_

#include <compare>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <map>
#include <optional>
#include <set>

namespace braid::probe {

// Standalone, single-sequence model of classic worker lease custody.
// Inputs represent evidence supplied by a test. This model does not collect
// native evidence, authenticate messages, or establish browser-wide coverage.
class ClassicWorkerCustody {
 public:
  using Id = uint64_t;

  struct Process {
    Id context;
    Id host;
    Id incarnation;
    auto operator<=>(const Process&) const = default;
  };

  struct Token {
    uint64_t high;
    uint64_t low;
    auto operator<=>(const Token&) const = default;
  };

  enum class Kind { kDocument, kDedicatedWorker };
  struct Original {
    Process process;
    Kind kind;
    Token token;
    // A document token alone does not identify a Blink window instance.
    Id generation;
    auto operator<=>(const Original&) const = default;
  };

  struct Lease {
    Original worker;
    Original parent;
    auto operator<=>(const Lease&) const = default;
  };

  enum class ScriptType { kUnknown, kClassic, kModule };
  enum class Unsupported { kShared, kService, kWorklet };
  enum class Refusal {
    kNone,
    kWrongPhase,
    kBeforeEnrollment,
    kLateIngress,
    kUnresolvedOriginal,
    kModuleWorker,
    kMissingScriptType,
    kSharedWorker,
    kServiceWorker,
    kWorklet,
    kDuplicateLaunch,
    kInvalidEvidence,
    kProcessLost,
    kStartUnresolved,
    kRetirementMissing,
    kForcedOrUndrained,
    kIdentityExhausted,
    kInventoryLimit,
  };

  ClassicWorkerCustody() = default;
  ClassicWorkerCustody(const ClassicWorkerCustody&) = delete;
  ClassicWorkerCustody& operator=(const ClassicWorkerCustody&) = delete;

  void Enroll() {
    if (enrolled_) {
      Refuse(Refusal::kWrongPhase);
    }
    // Never clears a refusal from before enrollment.
    enrolled_ = true;
  }

  // A real new launch, not a repeat Init() on a still-live host.
  std::optional<Process> BeginProcess(Id context, Id host) {
    if (!context || !host || processes_.contains(host)) {
      Refuse(Refusal::kInvalidEvidence);
      return {};
    }
    if (!HasRoom(processes_.size())) {
      return {};
    }
    if (Id incarnation = NextId()) {
      Process process{context, host, incarnation};
      processes_.emplace(host, process);
      return process;
    }
    return {};
  }

  // Unexpected process or proof-channel loss. An independently proven clean
  // process exit is a separate browser-finalization decision.
  void ProcessLost(Process source) {
    if (!Current(source)) {
      Refuse(Refusal::kInvalidEvidence);
      return;
    }
    Refuse(Refusal::kProcessLost);
    processes_.erase(source.host);
  }

  // Mirror exact document originals from the composing document ledger, only
  // for factory parenting. This ledger does not qualify document retirement.
  void DocumentLive(Original document) {
    if (!enrolled_ || prepared_ || document.kind != Kind::kDocument ||
        !document.generation || document.token == Token{} ||
        !Current(document.process)) {
      Refuse(Refusal::kInvalidEvidence);
      return;
    }
    if (HasRoom(documents_.size()) &&
        !documents_.try_emplace(document, true).second) {
      Refuse(Refusal::kInvalidEvidence);
    }
  }

  void DocumentGone(Original document) {
    auto it = documents_.find(document);
    if (it == documents_.end() || !it->second) {
      Refuse(Refusal::kInvalidEvidence);
      return;
    }
    it->second = false;
  }

  // Capture the original at receiver bind. A missing original is represented
  // by no factory ID; do not resolve again at the launch call.
  std::optional<Id> BindFactory(Process source, Original parent) {
    if (prepared_ || source != parent.process || !ParentLive(parent) ||
        !HasRoom(factories_.size())) {
      return {};
    }
    if (Id id = NextId()) {
      factories_.emplace(id, Factory{parent, false});
      return id;
    }
    return {};
  }

  // Observe at factory entry, even when Chromium will reject the request. A
  // missing lease is only a fixture result, never a reason to block Chromium.
  std::optional<Lease> Reserve(std::optional<Id> factory,
                               Process source,
                               Token token,
                               ScriptType requested_type) {
    if (!enrolled_) {
      Refuse(Refusal::kBeforeEnrollment);
      return {};
    }
    if (prepared_) {
      Refuse(Refusal::kLateIngress);
      return {};
    }
    auto it = factory ? factories_.find(*factory) : factories_.end();
    if (it == factories_.end() || it->second.parent.process != source ||
        !ParentLive(it->second.parent)) {
      Refuse(Refusal::kUnresolvedOriginal);
      return {};
    }
    if (it->second.used) {
      Refuse(Refusal::kDuplicateLaunch);
      return {};
    }
    it->second.used = true;
    if (requested_type != ScriptType::kClassic) {
      Refuse(requested_type == ScriptType::kModule
                 ? Refusal::kModuleWorker
                 : Refusal::kMissingScriptType);
      return {};
    }
    if (!HasRoom(workers_.size())) {
      return {};
    }
    if (token == Token{} || !worker_tokens_.insert(token).second) {
      Refuse(Refusal::kDuplicateLaunch);
      return {};
    }
    if (Id id = NextId()) {
      Lease lease{Original{source, Kind::kDedicatedWorker, token, id},
                  it->second.parent};
      workers_.emplace(id, Worker{lease, Phase::kReserved});
      return lease;
    }
    return {};
  }

  void ObserveUnsupported(Unsupported producer) {
    switch (producer) {
      case Unsupported::kShared:
        Refuse(Refusal::kSharedWorker);
        break;
      case Unsupported::kService:
        Refuse(Refusal::kServiceWorker);
        break;
      case Unsupported::kWorklet:
        Refuse(Refusal::kWorklet);
        break;
    }
  }

  bool BindBlinkScope(Process source,
                      const Lease& lease,
                      const Original& actual_parent,
                      Token actual_worker,
                      ScriptType scope_type) {
    if (actual_parent != lease.parent || actual_worker != lease.worker.token ||
        scope_type != ScriptType::kClassic) {
      Refuse(Refusal::kInvalidEvidence);
      return false;
    }
    return Advance(source, lease, Phase::kReserved, Phase::kBound);
  }

  // Sample the real top-level Script's type; do not echo the factory argument.
  bool BlinkEvaluated(Process source,
                      const Lease& lease,
                      ScriptType evaluated_type,
                      bool success) {
    if (!success || evaluated_type != ScriptType::kClassic) {
      Refuse(Refusal::kStartUnresolved);
      return false;
    }
    return Advance(source, lease, Phase::kBound, Phase::kRunning);
  }

  bool BlinkSealedIngress(Process source, const Lease& lease) {
    return Advance(source, lease, Phase::kRunning, Phase::kDisposing);
  }

  bool BlinkRetired(Process source,
                    const Lease& lease,
                    bool graceful_and_locally_drained) {
    if (!graceful_and_locally_drained) {
      Refuse(Refusal::kForcedOrUndrained);
      return false;
    }
    return Advance(source, lease, Phase::kDisposing, Phase::kAwaitingDrain);
  }

  // Represents ordered drain of the original endpoint, not just disconnect.
  bool DrainOriginalEndpoint(Process source, const Lease& lease) {
    return Advance(source, lease, Phase::kAwaitingDrain, Phase::kRetired);
  }

  // Known rejected, canceled or dropped launch; not generic host destruction.
  // Host destruction and DevTools detach cannot transition a lease.
  void StartLost() { Refuse(Refusal::kStartUnresolved); }

  void Prepare() {
    if (!enrolled_ || prepared_) {
      Refuse(Refusal::kWrongPhase);
    }
    prepared_ = true;
  }

  Refusal CheckTerminal() {
    if (!prepared_) {
      Refuse(Refusal::kWrongPhase);
    }
    for (const auto& [id, worker] : workers_) {
      switch (worker.phase) {
        case Phase::kReserved:
        case Phase::kBound:
          Refuse(Refusal::kStartUnresolved);
          break;
        case Phase::kRunning:
        case Phase::kDisposing:
        case Phase::kAwaitingDrain:
          Refuse(Refusal::kRetirementMissing);
          break;
        case Phase::kRetired:
          break;
      }
    }
    return refusal_;
  }

  Refusal refusal() const { return refusal_; }

 private:
  static constexpr std::size_t kInventoryLimit = 256;
  enum class Phase {
    kReserved,
    kBound,
    kRunning,
    kDisposing,
    kAwaitingDrain,
    kRetired,
  };
  struct Factory {
    Original parent;
    bool used;
  };
  struct Worker {
    Lease lease;
    Phase phase;
  };

  void Refuse(Refusal reason) {
    if (refusal_ == Refusal::kNone) {
      refusal_ = reason;
    }
  }

  bool HasRoom(std::size_t size) {
    if (size < kInventoryLimit) {
      return true;
    }
    Refuse(Refusal::kInventoryLimit);
    return false;
  }

  Id NextId() {
    if (last_id_ == std::numeric_limits<Id>::max()) {
      Refuse(Refusal::kIdentityExhausted);
      return 0;
    }
    return ++last_id_;
  }

  bool Current(Process process) const {
    auto it = processes_.find(process.host);
    return it != processes_.end() && it->second == process;
  }

  bool ParentLive(Original parent) const {
    if (!Current(parent.process)) {
      return false;
    }
    if (parent.kind == Kind::kDocument) {
      auto it = documents_.find(parent);
      return it != documents_.end() && it->second;
    }
    auto it = workers_.find(parent.generation);
    return it != workers_.end() && it->second.lease.worker == parent &&
           (it->second.phase == Phase::kBound ||
            it->second.phase == Phase::kRunning);
  }

  bool Advance(Process source, const Lease& lease, Phase expected, Phase next) {
    auto it = workers_.find(lease.worker.generation);
    if (!Current(source) || lease.worker.process != source ||
        it == workers_.end() || it->second.lease != lease ||
        it->second.phase != expected) {
      Refuse(Refusal::kInvalidEvidence);
      return false;
    }
    it->second.phase = next;
    return true;
  }

  bool enrolled_ = false;
  bool prepared_ = false;
  Refusal refusal_ = Refusal::kNone;
  Id last_id_ = 0;
  std::map<Id, Process> processes_;
  std::map<Original, bool> documents_;
  std::map<Id, Factory> factories_;
  std::set<Token> worker_tokens_;
  std::map<Id, Worker> workers_;
};

}  // namespace braid::probe

#endif  // BRAID_FIXTURE_CLASSIC_WORKER_CUSTODY_H_
