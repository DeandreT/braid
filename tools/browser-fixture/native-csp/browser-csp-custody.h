// Standalone test model for browser -> LocalFrame CSP custody. Not linked into
// Chromium or the package. Callers supply simulated identities and loss facts.
#pragma once

#include <cstddef>
#include <functional>
#include <map>
#include <optional>
#include <string>
#include <utility>

namespace braid::csp_custody {

struct Original {
  std::string run;
  std::string incarnation;
  std::string frame;
  std::string document;
  std::string lease;

  bool Valid() const {
    return !run.empty() && !incarnation.empty() && !frame.empty() &&
           !document.empty() && !lease.empty();
  }
  bool operator==(const Original&) const = default;
};

// Branch of this call, NOT the policy's enforce/report-only disposition.
enum class Path { kDom, kFrameAncestors };
enum class End { kRefused, kDispatchReturned, kReportAndIssuePathReturned };

struct Tag {
  std::string id;
  Original original;
  Path path;
};

struct Result {
  std::string id;
  End end;
};

// Models the browser-side ledger. Outbound metadata is a copy, not ownership
// of the reservation. No operation here can attest a whole run.
class SendLedger {
 public:
  static constexpr std::size_t kMaxEntries = 512;

  // `actual` is read from the original in-use RFH and its live producer lease,
  // not looked up by URL or routing node. Native caller supplies a fresh ID.
  std::optional<Tag> BeforeSend(std::string id,
                                const Original& intended,
                                const std::optional<Original>& actual,
                                Path path) {
    if (refused_ || sealed_ || !intended.Valid() || actual != intended ||
        id.empty() || entries_.size() >= kMaxEntries || entries_.contains(id)) {
      refused_ = true;
      return std::nullopt;
    }
    entries_.emplace(id, Entry{intended, path, std::nullopt});
    return Tag{std::move(id), intended, path};
  }

  // `from` is the original's identity derived independently from the
  // returning endpoint, not identity fields echoed in the result.
  void Observe(const Original& from, const Result& result) {
    auto it = entries_.find(result.id);
    if (it == entries_.end() || it->second.end) {
      refused_ = true;
      return;
    }
    auto& entry = it->second;
    const End expected = entry.path == Path::kDom
                             ? End::kDispatchReturned
                             : End::kReportAndIssuePathReturned;
    if (entry.original != from || result.end != expected) {
      Refuse(entry);
      return;
    }
    entry.end = result.end;
  }

  void LostOriginal(const Original& original) {
    for (auto& [id, entry] : entries_)
      if (entry.original == original && !entry.end)
        Refuse(entry);
  }

  void LostProcess(const std::string& incarnation) {
    for (auto& [id, entry] : entries_)
      if (entry.original.incarnation == incarnation && !entry.end)
        Refuse(entry);
  }

  // A late actual browser send still proceeds with no metadata.
  void SealIngress() { sealed_ = true; }
  void MissingTerminalResult() {
    for (auto& [id, entry] : entries_)
      if (!entry.end)
        Refuse(entry);
  }

  bool refused() const { return refused_; }
  std::size_t pending() const {
    std::size_t count = 0;
    for (const auto& [id, entry] : entries_)
      count += !entry.end;
    return count;
  }
  std::optional<End> outcome(const std::string& id) const {
    auto it = entries_.find(id);
    return it == entries_.end() ? std::nullopt : it->second.end;
  }

 private:
  struct Entry {
    Original original;
    Path path;
    std::optional<End> end;
  };

  void Refuse(Entry& entry) {
    entry.end = End::kRefused;
    refused_ = true;
  }

  std::map<std::string, Entry> entries_;
  bool refused_ = false;
  bool sealed_ = false;
};

// Models one renderer obligation moved into the simulated dispatch task.
// Destroying an unconsumed task refuses through the nonthrowing sink if it
// can still return; otherwise the browser record remains pending.
class ReceiveObligation {
 public:
  using Sink = std::function<void(Result)>;

  ReceiveObligation(Tag tag, Sink sink)
      : tag_(std::move(tag)), sink_(std::move(sink)) {}
  ReceiveObligation(ReceiveObligation&& other) noexcept
      : tag_(std::exchange(other.tag_, std::nullopt)),
        sink_(std::move(other.sink_)),
        stage_(other.stage_),
        original_document_(other.original_document_),
        event_(other.event_) {}
  ReceiveObligation(const ReceiveObligation&) = delete;
  ReceiveObligation& operator=(const ReceiveObligation&) = delete;
  ~ReceiveObligation() { Finish(End::kRefused); }

  // Actual native recipient identity and object at LocalFrame receive.
  void Received(const Original& actual, const void* document, Path path) {
    if (!tag_)
      return;
    if (stage_ != Stage::kSent || actual != tag_->original || !document ||
        path != tag_->path) {
      Finish(End::kRefused);
      return;
    }
    original_document_ = document;
    stage_ = Stage::kReceived;
  }

  void Queued(const Original& actual, const void* document) {
    if (!tag_)
      return;
    if (stage_ != Stage::kReceived || tag_->path != Path::kDom ||
        !MatchesOriginal(actual, document)) {
      Finish(End::kRefused);
      return;
    }
    stage_ = Stage::kQueued;
  }

  void EnterDispatch(const Original& actual,
                     const void* receiver,
                     const void* event) {
    if (!tag_)
      return;
    if (stage_ != Stage::kQueued || !MatchesOriginal(actual, receiver) ||
        !event) {
      Finish(End::kRefused);
      return;
    }
    event_ = event;
    stage_ = Stage::kInDispatch;
  }

  // Called only from the native phase probe. Nested/different events cannot
  // satisfy this obligation; a duplicate phase for the exact pair refuses.
  void AtNativePhase(const void* event, const void* receiver, bool trusted) {
    if (!tag_ || !event_ || event != event_ || receiver != original_document_)
      return;
    if (stage_ != Stage::kInDispatch || !trusted) {
      Finish(End::kRefused);
      return;
    }
    stage_ = Stage::kObserved;
  }

  void DispatchReturned(const Original& actual,
                        const void* receiver,
                        const void* event) {
    if (!tag_)
      return;
    Finish(stage_ == Stage::kObserved && event == event_ &&
                   MatchesOriginal(actual, receiver)
               ? End::kDispatchReturned
               : End::kRefused);
  }

  // Only at the actual non-DOM branch, after reporting and AddInspectorIssue
  // return; neither this result nor a report-only header promises delivery.
  void ReportAndIssuePathReturned(const Original& actual,
                                  const void* document) {
    if (!tag_)
      return;
    Finish(stage_ == Stage::kReceived && tag_->path == Path::kFrameAncestors &&
                   MatchesOriginal(actual, document)
               ? End::kReportAndIssuePathReturned
               : End::kRefused);
  }

 private:
  enum class Stage { kSent, kReceived, kQueued, kInDispatch, kObserved };

  bool MatchesOriginal(const Original& actual, const void* document) const {
    return actual == tag_->original && document &&
           document == original_document_;
  }

  void Finish(End end) {
    auto tag = std::exchange(tag_, std::nullopt);
    if (tag && sink_)
      sink_(Result{std::move(tag->id), end});
  }

  std::optional<Tag> tag_;
  Sink sink_;
  Stage stage_ = Stage::kSent;
  // Opaque witnesses in this model, never dereferenced. Blink must use a weak
  // persistent Document and keep the Event only on its dispatch stack.
  const void* original_document_ = nullptr;
  const void* event_ = nullptr;
};

}  // namespace braid::csp_custody
