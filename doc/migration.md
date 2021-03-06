# Migrating from MessagePack v1.x to MessagePack v2.x

MessagePack 2.0 contains many breaking changes since the 1.x versions.
These include both binary and source breaking changes, meaning you may need to update your source code as well as recompile against the 2.x version.

The v1.x version will still be serviced for security fixes, but new features will tend to only be offered in the 2.x versions.

Update your package references from the 1.x version you use to the 2.x version. If your project compiles, you may be done.
Otherwise work through each compiler error. Some common ones you may face are listed below with suggested fixes.

If you own an application that has a mix of MessagePack consumers and not all of them can be upgraded to v2.x at once, you can offer both MessagePack v1.x and v2.x assemblies with your application so that each user can find the one it needs. [Here is a sample](https://github.com/AArnott/MessagePackDualVersions).

## API changes

### MessagePackSerializerOptions

A new `MessagePackSerializerOptions` class becomes a first class citizen in this library.
It encapsulates the `IFormatterResolver` that used to be passed around by itself.
It also includes several other settings that may influence how `MessagePackSerializerOptions` or some of the
formatters may operate.

Because this new class tends to get saved to public static properties, it is immutable to ensure it can be shared safely.
Each property `Foo` on the class includes a `WithFoo` method which clones the instance and returns the new instance with just that one property changed.

To support this new options class and avoid unnecessary allocations from the copy-and-mutate methods, many of the popular resolvers now expose a public static `Options` property with the resolver preset to itself. So for example, you may use:

```cs
var msgpack = MessagePackSerializer.Serialize(objectGraph, StandardResolverAllowPrivate.Options);
var deserializedGraph = MessagePackSerializer.Deserialize<MyType>(msgpack, StandardResolverAllowPrivate.Options);
```

If you want to combine a particular resolver with other options changes (e.g. enabling LZ4 compression), you may do that too:

```cs
var options = StandardResolverAllowPrivate.Options.WithLZ4Compression(true);
var msgpack = MessagePackSerializer.Serialize(objectGraph, options);
var deserializedGraph = MessagePackSerializer.Deserialize<MyType>(msgpack, options);
```

An equivalent options instance can be created manually:

```cs
var options = MessagePackSerializerOptions.Standard
    .WithLZ4Compression(true)
    .WithResolver(StandardResolverAllowPrivate.Instance);
```

### MessagePackSerializer class

#### Serialization

Serializing object graphs to msgpack is now based on `IBufferWriter<byte>` instead of `ref byte[]`.
This allows for serializing very large object graphs without repeatedly allocating ever-larger arrays and copying the previously serialized msgpack bytes from the smaller buffer to the larger one.
`IBufferWriter<byte>` can direct the written msgpack bytes directly to a pipe, a file, or anywhere else you choose, allowing you to avoid a buffer copy within your own code as well.

An `IBufferWriter<byte>` is always wrapped by the new `MessagePackWriter` struct.

Many overloads of the `Serialize` method exist which ultimately all call the overload that accepts a `MessagePackWriter`.

#### Deserialization

Deserializing msgpack sequences is now much more flexible.
Instead of deserializing from `byte[]` or `ArraySegment<byte>` only, you can deserialize from any `ReadOnlyMemory<byte>` or `ReadOnlySequence<byte>` instance.

`ReadOnlyMemory<byte>` is like `ArraySegment<byte>` but more friendly and can refer to contiguous memory anywhere including native pointers. You can pass a `byte[]` or `ArraySegment<byte>` in anywhere that `ReadOnlyMemory<byte>` is expected and C# will implicitly cast for you (without any buffer copying).

`ReadOnlySequence<byte>` allows for deserialization from non-continguously allocated memory, enabling you to deserialize very large msgpack sequences without risking an `OutOfMemoryException` due simply to the inability to find large amounts of free contiguous memory.

Many overloads of the `Deserialize` method exists which ultimately all call the overload that accepts a `MessagePackReader`.

#### Default behavior

The `DefaultResolver` static property has been replaced with the `DefaultOptions` static property.
Just as with v1.x, in v2.x this static property influences how serialization occurs
when the value is not explicitly specified when invoking one of the `MessagePackSerializer` methods.

**WARNING**: When developing a simple application where you control all MessagePack-related code it may be safe to rely on this mutable static to control behavior.
For all other libraries or multi-purpose applications that use `MessagePackSerializer` you should explicitly specify the `MessagePackSerializerOptions` to use with each method invocation to guarantee your code behaves as you expect even when sharing an `AppDomain` or process with other MessagePack users that may change this static property.

#### Non-generic methods

In v1.x non-generic methods for serialization/deserialization were exposed on the nested `MessagePackSerializer.NonGeneric` class.
In v2.x these overloads are moved to the `MessagePackSerializer` class itself.

The `MessagePackSerializer.Typeless` nested class in v1.x remains in v2.x, but with a modified set of overloads.

#### JSON converting methods

In v1.x the `MessagePackSerializer` class exposed methods both to serialize an object graph to JSON,
as well as converting between msgpack and JSON. These two translations were very different but were mere overloads of each other.
In v2.x these methods have been renamed for clarity.
The methods `ConvertFromJson` and `ConvertToJson` translates between JSON and msgpack binary.
The method `SerializeToJson` translates an object graph to JSON.

#### LZ4MessagePackSerializer

The `LZ4MessagePackSerializer` class has been removed.
Instead, use `MessagePackSerializer` and pass in a `MessagePackSerializerOptions` with `UseLZ4Compression` set to true.

For example, make this change:

```diff
-byte[] buffer = LZ4MessagePackSerializer.Serialize("hi");
+byte[] buffer = MessagePackSerializer.Serialize("hi", MessagePackSerializerOptions.LZ4Standard);
```

### Thrown exceptions

In v1.x any exception thrown during serialization or deserialization was uncaught and propagated to the application.
In v2.x all exceptions are caught by the `MessagePackSerializer` and rethrown as an inner exception of `MessagePackSerializationException`.
This makes it easier to write code to catch exceptions during serialization since you can now catch just one specific type of exception.

### Built-in resolvers

The following resolvers have been *removed*:

| Removed v1.x formatter | v2.x alternative |
|--|--|
| `UnsafeBinaryResolver` | `NativeDecimalResolver`, `NativeGuidResolver`

#### CompositeResolver

In v1.x the `CompositeResolver` type could only be used once and mutated a static property.
In v2.x the `CompositeResolver` type no longer mutates any statics and thus can be used safely by many callers that simply want to aggregate many formatters and/or resolvers into one resolver. This often removes the need for you to define your own `IFormatterResolver`.

For example if you have written a custom formatter and want to use that in addition to what the `StandardResolver` offers, you can easily compose an aggregate resolver like this:

```cs
var resolver = CompositeResolver.Create(
    new IMessagePackFormatter[] { MyCustomFormatter.Instance },
    new IFormatterResolver[] { StandardResolver.Instance }
);
var options = MessagePackSerializerOptions.Standard.WithResolver(resolver);
var msgpack = MessagePackSerializer.Serialize(objectGraph, options);
var deserializedGraph = MessagePackSerializer.Deserialize<MyType>(msgpack, options);
```

### Built-in formatters

The following formatters have been *removed*:

| Removed v1.x formatter | v2.x alternative |
|--|--|
| `BinaryDecimalFormatter` | `NativeDecimalFormatter`
| `BinaryGuidFormatter` | `NativeGuidFormatter`
| `FourDimentionalArrayFormatter` | `FourDimensionalArrayFormatter`
| `OldSpecBinaryFormatter` | Use `MessagePackSerializerOptions.OldSpec` or `MessagePackWriter.OldSpec` instead.
| `OldSpecStringFormatter` | Use `MessagePackSerializerOptions.OldSpec` or `MessagePackWriter.OldSpec` instead.
| `QeueueFormatter<T>` | `QueueFormatter<T>`
| `TaskUnitFormatter` | Store values instead of promises
| `TaskValueFormatter<T>` | Store values instead of promises
| `ThreeDimentionalArrayFormatter` | `ThreeDimensionalArrayFormatter`
| `TwoDimentionalArrayFormatter` | `TwoDimensionalArrayFormatter`
| `ValueTaskFormatter<T>` | Store values instead of promises

A few formatters that remain have changed to remove mutable properties where those formatters may be exposed
as public static instances. This helps to avoid malfunctions when one MessagePack user changes a static setting
to suit their need but in a way that conflicts with another MessagePack user within the same process.

### Custom formatters

If you have written a custom `IMessagePackFormatter<T>` implementation you will have to adapt to the interface changes and APIs used to implement such a class.

The interface has been changed as described here:

```diff
 public interface IMessagePackFormatter<T> : IMessagePackFormatter
 {
-    int Serialize(ref byte[] bytes, int offset, T value, IFormatterResolver  formatterResolver);
+    void Serialize(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options);
-    T Deserialize(byte[] bytes, int offset, IFormatterResolver  formatterResolver, out int readSize);
+    T Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options);
 }
```

Notice the simpler method signature for each method.
You no longer have to deal with raw arrays and offsets.
The `MessagePackBinary` static class from v1.x that a formatter used to write msgpack codes is replaced with `MessagePackWriter` and `MessagePackReader`.
These two structs include the APIs to write and read msgpack, and they manage the underlying buffers so you no longer need to.

Consider the following v1.x formatter for the `Int16` type:

```cs
class NullableInt16Formatter : IMessagePackFormatter<Int16?>
{
    public int Serialize(ref byte[] bytes, int offset, Int16? value, IFormatterResolver formatterResolver)
    {
        if (value == null)
        {
            return MessagePackBinary.WriteNil(ref bytes, offset);
        }
        else
        {
            return MessagePackBinary.WriteInt16(ref bytes, offset, value.Value);
        }
    }

    public Int16? Deserialize(byte[] bytes, int offset, IFormatterResolver formatterResolver, out int readSize)
    {
        if (MessagePackBinary.IsNil(bytes, offset))
        {
            readSize = 1;
            return null;
        }
        else
        {
            return MessagePackBinary.ReadInt16(bytes, offset, out readSize);
        }
    }
}
```

After migration for v2.x, it looks like this:

```cs
class NullableInt16Formatter : IMessagePackFormatter<Int16?>
{
    public void Serialize(ref MessagePackWriter writer, Int16? value, MessagePackSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNil();
        }
        else
        {
            writer.Write(value.Value);
        }
    }

    public Int16? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
        {
            return default;
        }
        else
        {
            return reader.ReadInt16();
        }
    }
}
```

Notice the structure is very similar, but arrays and offsets are no longer necessary.
The underlying msgpack format is unchanged, allowing code to be upgraded to v2.x while maintaining
compatibility with a file or network party that uses MessagePack v1.x.
