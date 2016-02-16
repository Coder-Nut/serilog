﻿// Copyright 2013-2015 Serilog Contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.


using System;
#if REMOTING
using System.Runtime.Remoting.Messaging;
#endif
#if ASYNCLOCAL
using System.Collections.Generic;
using System.Threading;
#endif
using Serilog.Core;
using Serilog.Core.Enrichers;
using Serilog.Events;

namespace Serilog.Context
{
    /// <summary>
    /// Holds ambient properties that can be attached to log events. To
    /// configure, use the <see cref="LoggerConfigurationExtensions.FromLogContext"/>
    /// extension method.
    /// </summary>
    /// <example>
    /// Configuration:
    /// <code lang="C#">
    /// var log = new LoggerConfiguration()
    ///     .Enrich.FromLogContext()
    ///     ...
    /// </code>
    /// Usage:
    /// <code lang="C#">
    /// using (LogContext.PushProperty("MessageId", message.Id))
    /// {
    ///     Log.Information("The MessageId property will be attached to this event");
    /// }
    /// </code>
    /// </example>
    /// <remarks>The scope of the context is the current logical thread, using
#if ASYNCLOCAL
    /// <seealso cref="AsyncLocal{T}"/>
#else
    /// <seealso cref="CallContext.LogicalGetData"/>
#endif
    /// (and so is preserved across async/await calls).</remarks>
    public static class LogContext
    {
#if ASYNCLOCAL
        static readonly AsyncLocal<ImmutableStack<ILogEventEnricher>> Data = new AsyncLocal<ImmutableStack<ILogEventEnricher>>();
#else
#if DOTNET5_1
        [ThreadStatic]
        static ImmutableStack<ILogEventEnricher> Data;
#else
        static readonly string DataSlotName = typeof(LogContext).FullName;
#endif
#endif

        /// <summary>
        /// Push a property onto the context, returning an <see cref="IDisposable"/>
        /// that can later be used to remove the property, along with any others that
        /// may have been pushed on top of it and not yet popped. The property must
        /// be popped from the same thread/logical call context.
        /// </summary>
        /// <param name="name">The name of the property.</param>
        /// <param name="value">The value of the property.</param>
        /// <returns>A handle to later remove the property from the context.</returns>
        /// <param name="destructureObjects">If true, and the value is a non-primitive, non-array type,
        /// then the value will be converted to a structure; otherwise, unknown types will
        /// be converted to scalars, which are generally stored as strings.</param>
        /// <returns>A token that must be disposed, in order, to pop properties back off the stack.</returns>
        public static IDisposable PushProperty(string name, object value, bool destructureObjects = false)
        {
            var stack = GetOrCreateEnricherStack();
            var bookmark = new ContextStackBookmark(stack);

            Enrichers = stack.Push(new PropertyEnricher(name, value, destructureObjects));

            return bookmark;
        }

        /// <summary>
        /// Push multiple properties onto the context, returning an <see cref="IDisposable"/>
        /// that can later be used to remove the properties. The properties must
        /// be popped from the same thread/logical call context.
        /// </summary>
        /// <param name="properties">Log Properties to push onto the log context</param>
        /// <returns>A token that must be disposed, in order, to pop properties back off the stack.</returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static IDisposable PushProperties(params ILogEventEnricher[] properties)
        {
            if (properties == null) throw new ArgumentNullException(nameof(properties));

            var stack = GetOrCreateEnricherStack();
            var bookmark = new ContextStackBookmark(stack);

            foreach (var prop in properties)
                stack = stack.Push(prop);

            Enrichers = stack;

            return bookmark;
        }

        /// <summary>
        /// Remove all data from the context so that
        /// cross-<see cref="AppDomain"/> calls can be made without requiring
        /// Serilog assemblies to be present in the remote domain.
        /// </summary>
        /// <returns>A token that will restore the suspended log context data, if any.</returns>
        /// <remarks>The <see cref="LogContext"/> should not be manipulated further
        /// until the return value from this method has been disposed.</remarks>
        /// <returns></returns>
        public static IDisposable Suspend()
        {
            var stack = GetOrCreateEnricherStack();
            var bookmark = new ContextStackBookmark(stack);

            Enrichers = null;

            return bookmark;
        }

        static ImmutableStack<ILogEventEnricher> GetOrCreateEnricherStack()
        {
            var enrichers = Enrichers;
            if (enrichers == null)
            {
                enrichers = ImmutableStack<ILogEventEnricher>.Empty;
                Enrichers = enrichers;
            }
            return enrichers;
        }

#if ASYNCLOCAL
       static ImmutableStack<ILogEventEnricher> Enrichers
        {
            get
            {
                return Data.Value;
            }
            set
            {
                Data.Value = GetContext(value);
            }
        }
#else

#if DOTNET5_1
       static ImmutableStack<ILogEventEnricher> Enrichers
        {
            get
            {
                return Data;
            }
            set
            {
                Data = GetContext(value);
            }
        }

#else
        static ImmutableStack<ILogEventEnricher> Enrichers
        {
            get
            {
                var data = CallContext.LogicalGetData(DataSlotName);

                ImmutableStack<ILogEventEnricher> context;
#if REMOTING
                if (PermitCrossAppDomainCalls)
                {
                    context = ((Wrapper)data)?.Value;
                }
                else
                {
                    context = (ImmutableStack<ILogEventEnricher>)data;
                }
#else
                context = data;
#endif
                return context;
            }
            set
            {
                var context = GetContext(value);
                CallContext.LogicalSetData(DataSlotName, context);
            }
        }
#endif
#endif

#if REMOTING
        /// <summary>
        /// When calling into appdomains without Serilog loaded, e.g. via remoting or during unit testing,
        /// it may be necesary to set this value to true so that serialization exceptions are avoided. When possible,
        /// using the <see cref="Suspend"/> method in a using block around the call has a lower overhead and
        /// should be preferred.
        /// </summary>
        public static bool PermitCrossAppDomainCalls { get; set; }

        static object GetContext(ImmutableStack<ILogEventEnricher> value)
        {
            var context = !PermitCrossAppDomainCalls ? (object) value : new Wrapper
            {
                Value = value
            };
            return context;
        }
#else
        static ImmutableStack<ILogEventEnricher> GetContext(ImmutableStack<ILogEventEnricher> value)
        {
            return value;
        }
#endif

        internal static void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            var enrichers = Enrichers;
            if (enrichers == null || enrichers == ImmutableStack<ILogEventEnricher>.Empty)
                return;

            foreach (var enricher in enrichers)
            {
                enricher.Enrich(logEvent, propertyFactory);
            }
        }

        sealed class ContextStackBookmark : IDisposable
        {
            readonly ImmutableStack<ILogEventEnricher> _bookmark;

            public ContextStackBookmark(ImmutableStack<ILogEventEnricher> bookmark)
            {
                _bookmark = bookmark;
            }

            public void Dispose()
            {
                Enrichers = _bookmark;
            }
        }

#if REMOTING
        sealed class Wrapper : MarshalByRefObject
        {
            public ImmutableStack<ILogEventEnricher> Value { get; set; }
        }
#endif
    }
}