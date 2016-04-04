﻿// 
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.
// 
// Microsoft Bot Framework: http://botframework.com
// 
// Bot Builder SDK Github:
// https://github.com/Microsoft/BotBuilder
// 
// Copyright (c) Microsoft Corporation
// All rights reserved.
// 
// MIT License:
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

namespace Microsoft.Bot.Builder.Internals.Fibers
{
    public static class Serialization
    {
        public interface ISurrogateProvider : ISerializationSurrogate
        {
            bool Handles(Type type, StreamingContext context);
        }

        public sealed class StoreInstanceByTypeSurrogate : ISurrogateProvider
        {
            [Serializable]
            public sealed class ObjectReference : IObjectReference
            {
                public readonly Type Type = null;
                object IObjectReference.GetRealObject(StreamingContext context)
                {
                    var provider = (IServiceProvider)context.Context;
                    return provider.GetService(this.Type);
                }
            }

            bool ISurrogateProvider.Handles(Type type, StreamingContext context)
            {
                var provider = (IServiceProvider)context.Context;
                var instance = provider.GetService(type);
                return instance != null;
            }

            void ISerializationSurrogate.GetObjectData(object obj, SerializationInfo info, StreamingContext context)
            {
                var type = obj.GetType();
                info.SetType(typeof(ObjectReference));
                info.AddValue(nameof(ObjectReference.Type), type);
            }

            object ISerializationSurrogate.SetObjectData(object obj, SerializationInfo info, StreamingContext context, ISurrogateSelector selector)
            {
                throw new NotImplementedException();
            }
        }

        public sealed class StoreInstanceByFieldsSurrogate : ISurrogateProvider
        {
            public const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            bool ISurrogateProvider.Handles(Type type, StreamingContext context)
            {
                return !type.IsSerializable;
            }

            void ISerializationSurrogate.GetObjectData(object obj, SerializationInfo info, StreamingContext context)
            {
                var type = obj.GetType();
                var fields = type.GetFields(Flags);
                foreach (var field in fields)
                {
                    var value = field.GetValue(obj);
                    info.AddValue(field.Name, value);
                }
            }

            object ISerializationSurrogate.SetObjectData(object obj, SerializationInfo info, StreamingContext context, ISurrogateSelector selector)
            {
                var type = obj.GetType();
                var fields = type.GetFields(Flags);
                foreach (var field in fields)
                {
                    var value = info.GetValue(field.Name, field.FieldType);
                    field.SetValue(obj, value);
                }

                return obj;
            }
        }

        public sealed class SurrogateLogDecorator : ISurrogateProvider
        {
            private readonly HashSet<Type> visited = new HashSet<Type>();
            private readonly ISurrogateProvider inner;
            // TOOD: better tracing interface
            private readonly TraceListener trace;

            public SurrogateLogDecorator(ISurrogateProvider inner, TraceListener trace)
            {
                SetField.NotNull(out this.inner, nameof(inner), inner);
                SetField.NotNull(out this.trace, nameof(trace), trace);
            }
            public override string ToString()
            {
                return $"{this.GetType().Name}({this.inner})";
            }
            bool ISurrogateProvider.Handles(Type type, StreamingContext context)
            {
                return this.inner.Handles(type, context);
            }

            void ISerializationSurrogate.GetObjectData(object obj, SerializationInfo info, StreamingContext context)
            {
                this.Visit(obj);
                this.inner.GetObjectData(obj, info, context);
            }

            object ISerializationSurrogate.SetObjectData(object obj, SerializationInfo info, StreamingContext context, ISurrogateSelector selector)
            {
                this.Visit(obj);
                return this.inner.SetObjectData(obj, info, context, selector);
            }

            private void Visit(object obj)
            {
                var type = obj.GetType();
                if (this.visited.Add(type))
                {
                    var message = $"{this.inner.GetType().Name}: visiting {type}";
                    this.trace.WriteLine(message);
                }
            }
        }

        public sealed class SurrogateSelector : ISurrogateSelector
        {
            private readonly IReadOnlyList<ISurrogateProvider> providers;
            public SurrogateSelector(IReadOnlyList<ISurrogateProvider> providers)
            {
                SetField.NotNull(out this.providers, nameof(providers), providers);
            }

            void ISurrogateSelector.ChainSelector(ISurrogateSelector selector)
            {
                throw new NotImplementedException();
            }

            ISurrogateSelector ISurrogateSelector.GetNextSelector()
            {
                throw new NotImplementedException();
            }

            ISerializationSurrogate ISurrogateSelector.GetSurrogate(Type type, StreamingContext context, out ISurrogateSelector selector)
            {
                for (int index = 0; index < this.providers.Count; ++index)
                {
                    var provider = this.providers[index];
                    if (provider.Handles(type, context))
                    {
                        selector = this;
                        return provider;
                    }
                }

                selector = null;
                return null;
            }
        }
        public sealed class SimpleServiceLocator : IServiceProvider
        {
            private readonly Dictionary<Type, object> instanceByType;

            public SimpleServiceLocator(IEnumerable<object> instances)
            {
                this.instanceByType = instances.ToDictionary(o => o.GetType(), o => o);
            }
            object IServiceProvider.GetService(Type serviceType)
            {
                object service;
                if (this.instanceByType.TryGetValue(serviceType, out service))
                {
                    return service;
                }

                return null;
            }
        }
    }
}
