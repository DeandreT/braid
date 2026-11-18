/** Accept only the creation endpoint's exact path for the mutation's owner. */
export function isInvitationPath(
  value: unknown,
  allowedOrganizationId: string,
): value is string {
  if (typeof value !== "string") return false;
  const match =
    /^\/organizations\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/invitations#[0-9a-f]{64}$/.exec(
      value,
    );
  return (
    match !== null && match[0] === value && match[1] === allowedOrganizationId
  );
}
