import { Component, input } from "@angular/core";
import type { components } from "../api/generated";
import { type NativeLocator, nativeText } from "./projection-view";

@Component({
  selector: "braid-native-evidence",
  template: `
    @if (locator() || owner()) {
      <details class="native-evidence">
        <summary>Native evidence</summary>
        @if (locator(); as location) {
          <p>{{ describe(location) }}</p>
        }
        @if (owner(); as nativeOwner) {
          <p>Validator owner: {{ describeOwner(nativeOwner) }}</p>
        }
      </details>
    }
  `,
})
export class NativeEvidenceComponent {
  readonly locator = input<NativeLocator | null | undefined>();
  readonly owner = input<
    components["schemas"]["NativeDiagnosticOwnerV1"] | null | undefined
  >();
  readonly describe = nativeText;
  describeOwner(
    owner: components["schemas"]["NativeDiagnosticOwnerV1"],
  ): string {
    return JSON.stringify(owner);
  }
}
