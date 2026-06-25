/*
 * SofaBuffers C# - output flush sink.
 *
 * SPDX-License-Identifier: MIT
 */

namespace sofab;

/// <summary>
/// Sink that receives buffered bytes when an <see cref="OStream"/>'s buffer fills
/// (or when <see cref="OStream.Flush"/> is called).
/// </summary>
/// <remarks>
/// This is a delegate, so a sink can be written as a lambda or a method group —
/// for example <c>stream.Write</c> for a <see cref="System.IO.Stream"/>.
/// Implementing it lets a message larger than the output buffer (or larger than
/// RAM) be streamed out incrementally: the encoder hands the sink each full
/// buffer and then resumes at the start of the buffer.
/// <para>
/// The array is owned by the encoder and is reused after the call returns; a sink
/// that needs to retain the bytes must copy them.
/// </para>
/// </remarks>
/// <param name="data">the encoder's active buffer</param>
/// <param name="offset">start of the pending bytes</param>
/// <param name="length">number of pending bytes</param>
public delegate void FlushSink(byte[] data, int offset, int length);
