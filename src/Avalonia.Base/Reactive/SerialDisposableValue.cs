using System;

namespace Avalonia.Reactive
{
    internal struct SerialDisposableValue : IDisposable
    {
        private IDisposable? _current;

        /// <summary>
        /// Gets or sets the underlying disposable.
        /// </summary>
        /// <remarks>If the SerialDisposable has already been disposed, assignment to this property causes immediate disposal of the given disposable object. Assigning this property disposes the previous disposable object.</remarks>
        public IDisposable? Disposable
        {
            get => _current;
            set
            {
                _current?.Dispose();
                _current = value;
            }
        }

        /// <summary>
        /// Disposes the underlying disposable as well as all future replacements.
        /// </summary>
        public void Dispose()
        {
            _current?.Dispose();
        }
    }
}
