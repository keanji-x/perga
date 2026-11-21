namespace Perga.PolicyVm;

/// <summary>
/// Sandboxed JS prelude providing ONLY the frozen schema builder DSL (`t.*`).
/// All Fail-Closed validation, safety scanning, and `_meta` stamping execute in trusted host C#
/// (<see cref="TemplateDataValidator"/>), completely isolated from JS prototype/intrinsic mutation.
/// </summary>
internal static class SchemaPrelude
{
    internal const string Script = """
        (function () {
          const freeze = Object.freeze;
          const assign = Object.assign;
          const keys = Object.keys;
          const isArray = Array.isArray;
          const slice = Array.prototype.slice;

          function makeNode(kind, extra) {
            const node = assign({ __pergaSchema: true, kind, isOptional: false }, extra || {});
            node.optional = function () {
              return makeNode(kind, assign({}, extra || {}, { isOptional: true }));
            };
            return freeze(node);
          }

          const schemaBuilder = freeze({
            string: () => makeNode("string"),
            number: () => makeNode("number"),
            boolean: () => makeNode("boolean"),
            id: () => makeNode("id"),
            isoDate: () => makeNode("isoDate"),
            markdown: () => makeNode("markdown"),
            enum: (values) => {
              if (!isArray(values) || values.length === 0) {
                throw new TypeError("t.enum(values) requires a non-empty array of strings.");
              }
              const copy = slice.call(values);
              for (let i = 0; i < copy.length; i += 1) {
                if (typeof copy[i] !== "string") {
                  throw new TypeError("t.enum(values) requires a non-empty array of strings.");
                }
              }
              return makeNode("enum", { values: freeze(copy) });
            },
            ref: (target) => {
              if (target !== "task" && target !== "file") {
                throw new TypeError('t.ref(target) only supports "task" or "file".');
              }
              return makeNode("ref", { target });
            },
            array: (itemSchema) => {
              if (!itemSchema || !itemSchema.__pergaSchema) {
                throw new TypeError("t.array(itemSchema) requires a schema node.");
              }
              return makeNode("array", { item: itemSchema });
            },
            object: (shape) => {
              if (!shape || typeof shape !== "object" || isArray(shape)) {
                throw new TypeError("t.object(shape) requires a plain object mapping field names to schemas.");
              }
              const cleanShape = {};
              const propNames = keys(shape);
              for (let i = 0; i < propNames.length; i += 1) {
                const k = propNames[i];
                if (!shape[k] || !shape[k].__pergaSchema) {
                  throw new TypeError("t.object(shape)." + k + " must be a schema node.");
                }
                cleanShape[k] = shape[k];
              }
              return makeNode("object", { shape: freeze(cleanShape) });
            },
          });

          Object.defineProperty(globalThis, "t", {
            value: schemaBuilder,
            writable: false,
            configurable: false,
            enumerable: true,
          });
        })();
        """;
}
