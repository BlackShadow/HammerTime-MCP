using System;

namespace HammerTime.Mcp.Plugin
{
    internal sealed class BridgeCommandException : Exception
    {
        public string Code { get; }

        public BridgeCommandException(string code, string message) : base(message)
        {
            Code = code;
        }
    }
}
