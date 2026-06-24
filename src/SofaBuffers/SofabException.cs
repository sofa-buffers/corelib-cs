/*
 * SofaBuffers C# - exception type.
 *
 * SPDX-License-Identifier: MIT
 */

using System.IO;

namespace SofaBuffers;

/// <summary>
/// Thrown by the encoder and decoder on protocol or buffer errors.
/// </summary>
/// <remarks>
/// It extends <see cref="IOException"/> so it composes naturally with .NET I/O:
/// a flush sink that writes to a socket or file may itself throw
/// <see cref="IOException"/>, and generated marshal/unmarshal code can simply
/// propagate it. The specific <see cref="SofabError"/> is available via
/// <see cref="Error"/>.
/// </remarks>
public sealed class SofabException : IOException
{
    /// <summary>The error category that caused this exception.</summary>
    public SofabError Error { get; }

    /// <summary>Create an exception for the given error category.</summary>
    /// <param name="error">the error category</param>
    public SofabException(SofabError error)
        : base("sofab: " + error)
    {
        Error = error;
    }

    /// <summary>Create an exception for the given error category with extra detail.</summary>
    /// <param name="error">the error category</param>
    /// <param name="detail">human-readable context appended to the message</param>
    public SofabException(SofabError error, string detail)
        : base("sofab: " + error + " (" + detail + ")")
    {
        Error = error;
    }
}
