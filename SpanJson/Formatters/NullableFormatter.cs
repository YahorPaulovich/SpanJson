﻿using System;

namespace SpanJson.Formatters
{
    public sealed class NullableFormatter<T> : IJsonFormatter<T?>, IJsonCompositeFormatter<T> where T : struct
    {
        public static readonly NullableFormatter<T> Default = new NullableFormatter<T>();
        private static readonly IJsonFormatter<T> DefaultFormatter = FormatterHelper.GetDefaultFormatter<T>();

        public T? DeSerialize(ref JsonReader reader, IJsonFormatterResolver formatterResolver)
        {
            throw new NotImplementedException();
        }

        public void Serialize(ref JsonWriter writer, T? value, IJsonFormatterResolver formatterResolver)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }
            DefaultFormatter.Serialize(ref writer, value.Value, formatterResolver);
        }
    }
}