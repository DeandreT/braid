using System.Text;
using Weft.Deployment;

/// <summary>Direct core use in the fixed read-only child, never a generated assembly or request-selected type.</summary>
internal static class XmlComparisonInvocation
{
    internal static async Task<int> RunAsync(CancellationToken token)
    {
        var stage = "request";
        try
        {
            stage = "dependency";
            XmlComparisonProtocol.VerifyRuntimeDependencies(token);
            stage = "request";
            using var input = new MemoryStream();
            var buffer = new byte[65536];
            var stream = Console.OpenStandardInput();
            int count;
            while ((count = await stream.ReadAsync(buffer, token)) != 0)
            {
                if (count > XmlComparisonProtocol.MaximumRequestBytes - input.Length) throw new InvalidOperationException();
                input.Write(buffer, 0, count);
            }
            stage = "decode";
            var invocation = XmlComparisonProtocol.DecodeRequest(input.ToArray());
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            stage = "comparison";
            // The parent's process deadline kills/reaps this child even while these synchronous calls run.
            var response = XmlComparisonProtocol.Compare(invocation, token);
            stage = "response";
            await Console.OpenStandardOutput().WriteAsync(response, token);
            return 0;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            await Console.OpenStandardError().WriteAsync(Encoding.ASCII.GetBytes("WeftXmlComparisonFailed:" + stage + "\n"));
            return 1;
        }
    }
}
