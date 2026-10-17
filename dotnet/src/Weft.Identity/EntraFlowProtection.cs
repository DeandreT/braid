using Microsoft.AspNetCore.DataProtection;

namespace Weft.Identity;

// Standard SecureDataFormat deliberately homogenizes Unprotect failures. Record only a
// closed operation failure before that boundary so an unavailable admitted ring cannot
// be presented as a successful or merely anonymous protocol outcome.
internal sealed class EntraFlowProtection(IDataProtectionProvider underlying,
    Func<EntraBrowserClient.Operation> operation) : IDataProtectionProvider
{
    public IDataProtector CreateProtector(string purpose) =>
        new Protector(underlying.CreateProtector(purpose), operation);

    private sealed class Protector(IDataProtector underlying,
        Func<EntraBrowserClient.Operation> operation) : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => new Protector(underlying.CreateProtector(purpose), operation);
        public byte[] Protect(byte[] plaintext) => Transform(plaintext, unprotect: false);
        public byte[] Unprotect(byte[] protectedData) => Transform(protectedData, unprotect: true);

        private byte[] Transform(byte[] input, bool unprotect)
        {
            var current = operation();
            current.CheckActive();
            try { return unprotect ? underlying.Unprotect(input) : underlying.Protect(input); }
            catch (ServerSessionIdentityException error)
            {
                var kind = unprotect && error.Kind == ServerSessionIdentityFailureKind.IntegrityFailure
                    ? ServerSessionIdentityFailureKind.Denied : error.Kind;
                current.Refuse(kind);
                throw EntraErrors.Create(kind);
            }
            catch (Exception error) when (EntraErrors.Recoverable(error))
            {
                current.Refuse(ServerSessionIdentityFailureKind.Unavailable);
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
            }
        }
    }
}
