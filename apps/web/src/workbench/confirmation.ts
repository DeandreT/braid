export interface ConfirmationView {
  title: string;
  action: string;
  effect: string;
  afterLeaving: string;
  facts: readonly { label: string; value: string }[];
}
