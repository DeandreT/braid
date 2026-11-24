#include "browser-csp-custody.h"

#include <cassert>
#include <iostream>
#include <memory>

#ifdef NDEBUG
#error "The custody controls require active assertions."
#endif

namespace {

using namespace braid::csp_custody;

const Original kOriginal{"run", "process-1", "frame", "document", "lease"};
const Original kReplacement{"run", "process-1", "frame", "replacement",
                            "other-lease"};
const Original kOtherEndpoint{"run", "process-2", "other-frame", "document",
                              "lease"};
const Original kOtherLease{"run", "process-1", "frame", "document",
                           "other-lease"};

struct Fixture {
  SendLedger browser;
  int document = 0;
  int replacement = 0;
  int event = 0;
  int nested = 0;
  bool return_channel = true;

  ReceiveObligation Receive(std::string id,
                            Path path = Path::kDom,
                            Original endpoint = kOriginal) {
    auto tag = browser.BeforeSend(std::move(id), kOriginal, kOriginal, path);
    assert(tag && "test setup must reserve before simulating IPC");
    return ReceiveObligation(std::move(*tag), [this, endpoint](Result result) {
      if (return_channel)
        browser.Observe(endpoint, result);
    });
  }
};

void SamePayloadsDoNotShareCredit() {
  Fixture f;
  auto one = f.Receive("one");
  auto two = f.Receive("two");
  one.Received(kOriginal, &f.document, Path::kDom);
  two.Received(kOriginal, &f.document, Path::kDom);
  one.Queued(kOriginal, &f.document);
  two.Queued(kOriginal, &f.document);
  auto task1 = std::move(one);
  auto task2 = std::move(two);
  assert(f.browser.pending() == 2);
  f.browser.SealIngress();

  // The model uses no payload fields. Completing the second task cannot
  // settle the first even if Chromium constructed identical event data.
  task2.EnterDispatch(kOriginal, &f.document, &f.event);
  task2.AtNativePhase(&f.nested, &f.document, true);
  task2.AtNativePhase(&f.event, &f.replacement, true);
  task2.AtNativePhase(&f.event, &f.document, true);
  assert(f.browser.pending() == 2);  // Phase is not dispatch return.
  task2.DispatchReturned(kOriginal, &f.document, &f.event);
  assert(f.browser.outcome("two") == End::kDispatchReturned);
  assert(!f.browser.outcome("one"));
  task1.EnterDispatch(kOriginal, &f.document, &f.nested);
  task1.AtNativePhase(&f.nested, &f.document, true);
  task1.DispatchReturned(kOriginal, &f.document, &f.nested);
  assert(f.browser.outcome("one") == End::kDispatchReturned);
  assert(!f.browser.refused() && f.browser.pending() == 0);
}

void OnlyTheExactNativePhaseCanReturn() {
  for (int bad = 0; bad != 4; ++bad) {
    Fixture f;
    auto work = f.Receive("event");
    work.Received(kOriginal, &f.document, Path::kDom);
    work.Queued(kOriginal, &f.document);
    work.EnterDispatch(kOriginal, &f.document, &f.event);
    if (bad == 0)
      work.AtNativePhase(&f.nested, &f.document, true);
    if (bad == 1)
      work.AtNativePhase(&f.event, &f.replacement, true);
    if (bad == 2)
      work.AtNativePhase(&f.event, &f.document, false);
    if (bad == 3) {
      work.AtNativePhase(&f.event, &f.document, true);
      work.AtNativePhase(&f.event, &f.document, true);
    }
    work.DispatchReturned(kOriginal, &f.document, &f.event);
    assert(f.browser.refused());
    assert(f.browser.outcome("event") == End::kRefused);
  }
}

void RefuseWrongOriginalAtEveryHandoff() {
  for (int boundary = 0; boundary != 5; ++boundary) {
    Fixture f;
    auto work = f.Receive("event", Path::kDom,
                          boundary == 4 ? kOtherEndpoint : kOriginal);
    work.Received(boundary == 0 ? kReplacement : kOriginal, &f.document,
                  Path::kDom);
    work.Queued(boundary == 1 ? kOtherLease : kOriginal, &f.document);
    work.EnterDispatch(kOriginal, boundary == 2 ? &f.replacement : &f.document,
                       &f.event);
    work.AtNativePhase(&f.event, &f.document, true);
    work.DispatchReturned(boundary == 3 ? kReplacement : kOriginal, &f.document,
                          &f.event);
    assert(f.browser.refused());
    assert(f.browser.outcome("event") == End::kRefused);
  }
}

void RefuseDroppedOrUnobservableWork() {
  for (bool channel : {true, false}) {
    Fixture f;
    f.return_channel = channel;
    {
      auto work = f.Receive("dropped");
      work.Received(kOriginal, &f.document, Path::kDom);
      work.Queued(kOriginal, &f.document);
      auto dropped_task = std::make_unique<ReceiveObligation>(std::move(work));
      assert(f.browser.pending() == 1);
    }
    assert(f.browser.pending() == (channel ? 0u : 1u));
    f.browser.MissingTerminalResult();
    assert(f.browser.refused());
    assert(f.browser.outcome("dropped") == End::kRefused);
  }

  Fixture f;
  assert(f.browser.BeforeSend("lost", kOriginal, kOriginal, Path::kDom));
  f.browser.LostOriginal(kOriginal);  // No IPC ever arrived.
  f.browser.Observe(kOriginal, {"lost", End::kDispatchReturned});
  assert(f.browser.outcome("lost") == End::kRefused);
  assert(f.browser.refused());

  Fixture g;
  assert(g.browser.BeforeSend("crash", kOriginal, kOriginal, Path::kDom));
  assert(g.browser.BeforeSend("other", kOtherEndpoint, kOtherEndpoint,
                              Path::kDom));
  g.browser.LostProcess(kOriginal.incarnation);
  assert(g.browser.outcome("crash") == End::kRefused);
  assert(!g.browser.outcome("other"));
}

void AncestorsNeverEarnsDomCredit() {
  Fixture f;
  auto work = f.Receive("ancestor", Path::kFrameAncestors);
  work.Received(kOriginal, &f.document, Path::kFrameAncestors);
  // Receiving the Mojo call does not settle the obligation.
  assert(f.browser.pending() == 1);
  work.ReportAndIssuePathReturned(kOriginal, &f.document);
  assert(f.browser.outcome("ancestor") == End::kReportAndIssuePathReturned);
  assert(!f.browser.refused());

  Fixture g;
  auto misrouted = g.Receive("ancestor", Path::kFrameAncestors);
  misrouted.Received(kOriginal, &g.document, Path::kFrameAncestors);
  misrouted.Queued(kOriginal, &g.document);
  assert(g.browser.outcome("ancestor") == End::kRefused);
  assert(g.browser.refused());

  Fixture h;
  auto dom = h.Receive("report-only-form", Path::kDom);
  dom.Received(kOriginal, &h.document, Path::kDom);
  dom.ReportAndIssuePathReturned(kOriginal, &h.document);
  assert(h.browser.outcome("report-only-form") == End::kRefused);
  assert(h.browser.refused());

  Fixture i;
  {
    auto missing_branch = i.Receive("suppressed", Path::kFrameAncestors);
    missing_branch.Received(kOriginal, &i.document, Path::kFrameAncestors);
    // No native PostViolationReport / AddInspectorIssue path.
  }
  assert(i.browser.outcome("suppressed") == End::kRefused);
  assert(i.browser.refused());
}

void RefuseDuplicateLateOrUnboundSend() {
  for (const auto& actual :
       {std::optional<Original>{}, std::optional{kReplacement},
        std::optional{kOtherLease}}) {
    SendLedger browser;
    assert(!browser.BeforeSend("unbound", kOriginal, actual, Path::kDom));
    assert(browser.refused());
    assert(browser.pending() == 0);
  }
  SendLedger duplicate;
  assert(duplicate.BeforeSend("once", kOriginal, kOriginal, Path::kDom));
  assert(!duplicate.BeforeSend("once", kOriginal, kOriginal, Path::kDom));
  assert(duplicate.refused());
  SendLedger replay;
  assert(replay.BeforeSend("once", kOriginal, kOriginal, Path::kDom));
  replay.Observe(kOriginal, {"once", End::kDispatchReturned});
  replay.Observe(kOriginal, {"once", End::kDispatchReturned});
  assert(replay.refused());
  SendLedger late;
  late.SealIngress();
  assert(!late.BeforeSend("late", kOriginal, kOriginal, Path::kDom));
  assert(late.refused());
}

void BrowserRejectsUnboundOrWrongKindResults() {
  SendLedger browser;
  assert(browser.BeforeSend("dom", kOriginal, kOriginal, Path::kDom));
  assert(browser.BeforeSend("ancestor", kOriginal, kOriginal,
                            Path::kFrameAncestors));
  browser.Observe(kOriginal, {"unknown", End::kDispatchReturned});
  assert(browser.refused() && browser.pending() == 2);
  browser.Observe(kOriginal, {"dom", End::kReportAndIssuePathReturned});
  browser.Observe(kOriginal, {"ancestor", End::kDispatchReturned});
  assert(browser.outcome("dom") == End::kRefused);
  assert(browser.outcome("ancestor") == End::kRefused);
  assert(browser.pending() == 0);
}

void ReplacementCannotInheritPendingResult() {
  SendLedger browser;
  assert(browser.BeforeSend("old", kOriginal, kOriginal, Path::kDom));
  assert(browser.BeforeSend("new", kReplacement, kReplacement, Path::kDom));
  browser.LostOriginal(kOriginal);
  // The replacement can return its own result, not the old one. The old
  // loss stays sticky even if both IDs later produce a result.
  browser.Observe(kReplacement, {"old", End::kDispatchReturned});
  browser.Observe(kReplacement, {"new", End::kDispatchReturned});
  browser.Observe(kOriginal, {"old", End::kDispatchReturned});
  assert(browser.outcome("old") == End::kRefused);
  assert(browser.outcome("new") == End::kDispatchReturned);
  assert(browser.refused() && browser.pending() == 0);
}

}  // namespace

int main() {
  SamePayloadsDoNotShareCredit();
  OnlyTheExactNativePhaseCanReturn();
  RefuseWrongOriginalAtEveryHandoff();
  RefuseDroppedOrUnobservableWork();
  AncestorsNeverEarnsDomCredit();
  RefuseDuplicateLateOrUnboundSend();
  BrowserRejectsUnboundOrWrongKindResults();
  ReplacementCannotInheritPendingResult();
  std::cout << "8 browser CSP custody controls passed\n";
}
