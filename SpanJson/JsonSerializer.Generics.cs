﻿using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SpanJson.Resolvers;

namespace SpanJson
{
    /// <summary>
    ///     Main Type for SpanJson Serializer
    /// </summary>
    public static partial class JsonSerializer
    {
        /// <summary>
        ///     Generic part
        /// </summary>
        public static class Generic
        {
            /// <summary>
            ///     This method is used for the nongeneric deserialize calls.
            /// </summary>
            internal static T DeserializeInternal<T, TSymbol, TResolver>(ReadOnlySpan<TSymbol> input)
                where TResolver : IJsonFormatterResolver<TSymbol, TResolver>, new() where TSymbol : struct
            {
                return Inner<T, TSymbol, TResolver>.InnerDeserialize(input);
            }


            private static class Inner<T, TSymbol, TResolver> where TResolver : IJsonFormatterResolver<TSymbol, TResolver>, new() where TSymbol : struct
            {
                private static readonly IJsonFormatter<T, TSymbol, TResolver> Formatter = StandardResolvers.GetResolver<TSymbol, TResolver>().GetFormatter<T>();

                public static string InnerSerializeToString(T input)
                {
                    var jsonWriter = new JsonWriter<TSymbol>(_lastSerializationSize);
                    Formatter.Serialize(ref jsonWriter, input, 0);
                    _lastSerializationSize = jsonWriter.Position;
                    var result = jsonWriter.ToString(); // includes Dispose
                    return result;
                }

                public static byte[] InnerSerializeToByteArray(T input)
                {
                    var jsonWriter = new JsonWriter<TSymbol>(_lastSerializationSize);
                    Formatter.Serialize(ref jsonWriter, input, 0);
                    _lastSerializationSize = jsonWriter.Position;
                    var result = jsonWriter.ToByteArray();
                    return result;
                }

                public static ValueTask InnerSerializeAsync(T input, TextWriter writer, CancellationToken cancellationToken = default)
                {
                    var jsonWriter = new JsonWriter<TSymbol>(_lastSerializationSize);
                    Formatter.Serialize(ref jsonWriter, input, 0);
                    _lastSerializationSize = jsonWriter.Position;
                    var temp = jsonWriter.Data;
                    var data = Unsafe.As<TSymbol[], char[]>(ref temp);
                    var result = writer.WriteAsync(data, 0, _lastSerializationSize);
                    if (result.IsCompletedSuccessfully)
                    {
                        // This is a bit ugly, as we use the arraypool outside of the jsonwriter, but ref can't be use in async
                        ArrayPool<char>.Shared.Return(data);
                        return new ValueTask();
                    }

                    return AwaitSerializeAsync(result, data);
                }

                public static ValueTask InnerSerializeAsync(T input, Stream stream, CancellationToken cancellationToken = default)
                {
                    var jsonWriter = new JsonWriter<TSymbol>(_lastSerializationSize);
                    Formatter.Serialize(ref jsonWriter, input, 0);
                    _lastSerializationSize = jsonWriter.Position;
                    var temp = jsonWriter.Data;
                    var data = Unsafe.As<TSymbol[], byte[]>(ref temp);
                    var result = stream.WriteAsync(data, 0, _lastSerializationSize, cancellationToken);
                    if (result.IsCompletedSuccessfully)
                    {
                        // This is a bit ugly, as we use the arraypool outside of the jsonwriter, but ref can't be use in async
                        ArrayPool<byte>.Shared.Return(data);
                        return new ValueTask();
                    }

                    return AwaitSerializeAsync(result, data);
                }

                public static T InnerDeserialize(ReadOnlySpan<TSymbol> input)
                {
                    _lastDeserializationSize = input.Length;
                    var jsonReader = new JsonReader<TSymbol>(input);
                    return Formatter.Deserialize(ref jsonReader);
                }

                public static ValueTask<T> InnerDeserializeAsync(TextReader reader, CancellationToken cancellationToken = default)
                {
                    var input = reader.ReadToEndAsync();
                    if (input.IsCompletedSuccessfully)
                    {
                        return new ValueTask<T>(InnerDeserialize(MemoryMarshal.Cast<char, TSymbol>(input.Result)));
                    }

                    return AwaitDeserializeAsync(input);
                }

                public static ValueTask<T> InnerDeserializeAsync(Stream stream, CancellationToken cancellationToken = default)
                {
                    if (stream is MemoryStream ms && ms.TryGetBuffer(out var buffer))
                    {
                        var span = new ReadOnlySpan<byte>(buffer.Array, buffer.Offset, buffer.Count);
                        return new ValueTask<T>(InnerDeserialize(MemoryMarshal.Cast<byte, TSymbol>(span)));
                    }

                    var input = stream.CanSeek
                        ? ReadStreamFullAsync(stream, cancellationToken)
                        : ReadStreamAsync(stream, _lastDeserializationSize, cancellationToken);
                    if (input.IsCompletedSuccessfully)
                    {
                        var memory = input.Result;
                        return new ValueTask<T>(InnerDeserialize(memory));
                    }

                    return AwaitDeserializeAsync(input);
                }

                private static async ValueTask<Memory<byte>> ReadStreamFullAsync(Stream stream, CancellationToken cancellationToken = default)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent((int) stream.Length);
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    return new Memory<byte>(buffer, 0, read);
                }

                private static T InnerDeserialize(Memory<byte> memory)
                {
                    var result = InnerDeserialize(MemoryMarshal.Cast<byte, TSymbol>(memory.Span));
                    if (MemoryMarshal.TryGetArray<byte>(memory, out var segment))
                    {
                        ArrayPool<byte>.Shared.Return(segment.Array);
                    }

                    return result;
                }

                private static async ValueTask<Memory<byte>> ReadStreamAsync(Stream stream, int sizeHint, CancellationToken cancellationToken = default)
                {
                    var totalSize = 0;
                    var buffer = ArrayPool<byte>.Shared.Rent(sizeHint);
                    int read;
                    while ((read = await stream.ReadAsync(buffer, totalSize, buffer.Length - totalSize, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        if (totalSize + read == buffer.Length)
                        {
                            Grow(ref buffer);
                        }

                        totalSize += read;
                    }

                    return new Memory<byte>(buffer, 0, totalSize);
                }

                private static void Grow(ref byte[] array)
                {
                    var backup = array;
                    array = ArrayPool<byte>.Shared.Rent(backup.Length * 2);
                    backup.CopyTo(array, 0);
                    ArrayPool<byte>.Shared.Return(backup);
                }

                // This is a bit ugly, as we use the arraypool outside of the jsonwriter, but ref can't be use in async
                private static async ValueTask AwaitSerializeAsync(Task result, char[] data)
                {
                    await result.ConfigureAwait(false);
                    ArrayPool<char>.Shared.Return(data);
                }

                // This is a bit ugly, as we use the arraypool outside of the jsonwriter, but ref can't be use in async
                private static async ValueTask AwaitSerializeAsync(Task result, byte[] data)
                {
                    await result.ConfigureAwait(false);
                    ArrayPool<byte>.Shared.Return(data);
                }

                private static async ValueTask<T> AwaitDeserializeAsync(Task<string> task)
                {
                    var input = await task.ConfigureAwait(false);
                    return InnerDeserialize(MemoryMarshal.Cast<char, TSymbol>(input));
                }

                private static async ValueTask<T> AwaitDeserializeAsync(ValueTask<Memory<byte>> task)
                {
                    var input = await task.ConfigureAwait(false);
                    return InnerDeserialize(input);
                }

                // ReSharper disable StaticMemberInGenericType
                private static int _lastSerializationSize = 256; // initial size, get's updated with each serialization

                private static int _lastDeserializationSize = 256; // initial size, get's updated with each deserialization
                // ReSharper restore StaticMemberInGenericType
            }

            /// <summary>
            ///     Serialize/Deserialize to/from string et al.
            /// </summary>
            public static class Utf16
            {
                /// <summary>
                ///     Serialize to string.
                /// </summary>
                /// <typeparam name="T">Type</typeparam>
                /// <param name="input">Input</param>
                /// <returns>String</returns>
                public static string Serialize<T>(T input)
                {
                    return Serialize<T, ExcludeNullsOriginalCaseResolver<char>>(input);
                }

                /// <summary>
                ///     Serialize to string with specific resolver.
                /// </summary>
                /// <typeparam name="T">Type</typeparam>
                /// <typeparam name="TResolver">Resolver</typeparam>
                /// <param name="input">Input</param>
                /// <returns>String</returns>
                public static string Serialize<T, TResolver>(T input)
                    where TResolver : IJsonFormatterResolver<char, TResolver>, new()
                {
                    return Inner<T, char, TResolver>.InnerSerializeToString(input);
                }

                /// <summary>
                ///     Serialize to TextWriter.
                /// </summary>
                /// <typeparam name="T">Type</typeparam>
                /// <param name="input">Input</param>
                /// <param name="writer">Writer</param>
                /// <param name="cancellationToken">CancellationToken</param>
                /// <returns>Task</returns>
                public static ValueTask SerializeAsync<T>(T input, TextWriter writer, CancellationToken cancellationToken = default)
                {
                    return SerializeAsync<T, ExcludeNullsOriginalCaseResolver<char>>(input, writer, cancellationToken);
                }

                /// <summary>
                ///     Deserialize to string with specific resolver.
                /// </summary>
                /// <typeparam name="T">Type</typeparam>
                /// <typeparam name="TResolver">Resolver</typeparam>
                /// <param name="input">Input</param>
                /// <returns>Deserialized object</returns>
                public static T Deserialize<T, TResolver>(ReadOnlySpan<char> input)
                    where TResolver : IJsonFormatterResolver<char, TResolver>, new()
                {
                    return Inner<T, char, TResolver>.InnerDeserialize(input);
                }

                /// <summary>
                ///     Deserialize to string.
                /// </summary>
                /// <typeparam name="T">Type</typeparam>
                /// <param name="input">Input</param>
                /// <returns>Deserialized object</returns>
                public static T Deserialize<T>(ReadOnlySpan<char> input)
                {
                    return Deserialize<T, ExcludeNullsOriginalCaseResolver<char>>(input);
                }

                /// <summary>
                ///     Deserialize from TextReader.
                /// </summary>
                /// <typeparam name="T">Type</typeparam>
                /// <param name="reader">TextReader</param>
                /// <param name="cancellationToken">CancellationToken</param>
                /// <returns>Deserialized object</returns>
                public static ValueTask<T> DeserializeAsync<T>(TextReader reader, CancellationToken cancellationToken = default)
                {
                    return DeserializeAsync<T, ExcludeNullsOriginalCaseResolver<char>>(reader, cancellationToken);
                }

                /// <summary>
                ///     Serialize to TextWriter with specific resolver.
                /// </summary>
                /// <typeparam name="T">Type</typeparam>
                /// <typeparam name="TResolver">Resolver</typeparam>
                /// <param name="input">Input</param>
                /// <param name="writer">Writer</param>
                /// <param name="cancellationToken">CancellationToken</param>
                /// <returns>Task</returns>
                public static ValueTask SerializeAsync<T, TResolver>(T input, TextWriter writer, CancellationToken cancellationToken = default)
                    where TResolver : IJsonFormatterResolver<char, TResolver>, new()
                {
                    return Inner<T, char, TResolver>.InnerSerializeAsync(input, writer, cancellationToken);
                }

                /// <summary>
                ///     Deserialize from TextReader with specific resolver.
                /// </summary>
                /// <typeparam name="T">Type</typeparam>
                /// <typeparam name="TResolver">Resolver</typeparam>
                /// <param name="reader">TextReader</param>
                /// <param name="cancellationToken">CancellationToken</param>
                /// <returns>Task</returns>
                public static ValueTask<T> DeserializeAsync<T, TResolver>(TextReader reader, CancellationToken cancellationToken = default)
                    where TResolver : IJsonFormatterResolver<char, TResolver>, new()
                {
                    return Inner<T, char, TResolver>.InnerDeserializeAsync(reader, cancellationToken);
                }
            }

            /// <summary>
            ///     Serialize/Deserialize to/from byte array et al.
            /// </summary>
            public static class Utf8
            {
                /// <summary>
                ///     Serialize to byte array.
                /// </summary>
                /// <typeparam name="T">Type</typeparam>
                /// <param name="input">Input</param>
                /// <returns>Byte array</returns>
                public static byte[] Serialize<T>(T input)
                {
                    return Serialize<T, ExcludeNullsOriginalCaseResolver<byte>>(input);
                }

                /// <summary>
                ///     Deserialize from byte array.
                /// </summary>
                /// <typeparam name="T">Type</typeparam>
                /// <param name="input">Input</param>
                /// <returns>Deserialized object</returns>
                public static T Deserialize<T>(ReadOnlySpan<byte> input)
                {
                    return Deserialize<T, ExcludeNullsOriginalCaseResolver<byte>>(input);
                }

                /// <summary>
                ///     Deserialize from byte array with specific resolver.
                /// </summary>
                /// <typeparam name="T">Type</typeparam>
                /// <typeparam name="TResolver">Resolver</typeparam>
                /// <param name="input">Input</param>
                /// <returns>Deserialized object</returns>
                public static T Deserialize<T, TResolver>(ReadOnlySpan<byte> input)
                    where TResolver : IJsonFormatterResolver<byte, TResolver>, new()
                {
                    return Inner<T, byte, TResolver>.InnerDeserialize(input);
                }

                /// <summary>
                ///     Serialize to byte array with specific resolver.
                /// </summary>
                /// <typeparam name="T">Type</typeparam>
                /// <typeparam name="TResolver">Resolver</typeparam>
                /// <param name="input">Input</param>
                /// <returns>Byte array</returns>
                public static byte[] Serialize<T, TResolver>(T input)
                    where TResolver : IJsonFormatterResolver<byte, TResolver>, new()
                {
                    return Inner<T, byte, TResolver>.InnerSerializeToByteArray(input);
                }

                /// <summary>
                ///     Serialize to stream.
                /// </summary>
                /// <typeparam name="T">Type</typeparam>
                /// <param name="input">Input</param>
                /// <param name="stream">Stream</param>
                /// <param name="cancellationToken">CancellationToken</param>
                /// <returns>Task</returns>
                public static ValueTask SerializeAsync<T>(T input, Stream stream, CancellationToken cancellationToken = default)
                {
                    return SerializeAsync<T, ExcludeNullsOriginalCaseResolver<byte>>(input, stream, cancellationToken);
                }

                /// <summary>
                ///     Deserialize from stream.
                /// </summary>
                /// <typeparam name="T">Type</typeparam>
                /// <param name="stream">Stream</param>
                /// <param name="cancellationToken">CancellationToken</param>
                /// <returns>Task</returns>
                public static ValueTask<T> DeserializeAsync<T>(Stream stream, CancellationToken cancellationToken = default)
                {
                    return DeserializeAsync<T, ExcludeNullsOriginalCaseResolver<byte>>(stream, cancellationToken);
                }

                /// <summary>
                ///     Serialize to stream with specific resolver.
                /// </summary>
                /// <typeparam name="T">Type</typeparam>
                /// <typeparam name="TResolver">Resolver</typeparam>
                /// <param name="input">Input</param>
                /// <param name="stream">Stream</param>
                /// <param name="cancellationToken">CancellationToken</param>
                /// <returns>Task</returns>
                public static ValueTask SerializeAsync<T, TResolver>(T input, Stream stream, CancellationToken cancellationToken = default)
                    where TResolver : IJsonFormatterResolver<byte, TResolver>, new()
                {
                    return Inner<T, byte, TResolver>.InnerSerializeAsync(input, stream, cancellationToken);
                }

                /// <summary>
                ///     Deserialize from stream with specific resolver.
                /// </summary>
                /// <typeparam name="T">Type</typeparam>
                /// <typeparam name="TResolver">Resolver</typeparam>
                /// <param name="stream">Stream</param>
                /// <param name="cancellationToken">CancellationToken</param>
                /// <returns>Task</returns>
                public static ValueTask<T> DeserializeAsync<T, TResolver>(Stream stream, CancellationToken cancellationToken = default)
                    where TResolver : IJsonFormatterResolver<byte, TResolver>, new()
                {
                    return Inner<T, byte, TResolver>.InnerDeserializeAsync(stream, cancellationToken);
                }
            }
        }
    }
}