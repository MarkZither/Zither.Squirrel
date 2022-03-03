using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Squirrel
{
    /// <summary>
    /// A simple way to pass and update progress state. Could be expanded to handle tier progress.
    /// </summary>
    public class ProgressContext
    {
        /// <summary>
        /// Called every time the progress value is updated 
        /// </summary>
        public event Action<int> OnProgressChanged;

        /// <summary>
        /// The amount to be added to Current every time Increament() is called
        /// </summary>
        public double IncreamentSize { get; set; }

        /// <summary>
        /// The current progress value
        /// </summary>
        public double Current { get; set; }

        object _lock = new();

        /// <summary>
        /// 
        /// </summary>
        /// <param name="onProgressChanged">Called back for when progress is updated</param>
        /// <param name="increment">The default value to increament progress by when Increament() is called</param>
        public ProgressContext(Action<int> onProgressChanged, double increment)
        {
            OnProgressChanged = onProgressChanged;
            IncreamentSize = increment;
        }

        /// <summary>
        /// Increament the current progress
        /// </summary>
        public void Increament()
        {
            Increament(IncreamentSize);
        }

        /// <summary>
        /// Increament the current progress by a given amount
        /// </summary>
        /// <param name="increment">The amount to increament the progress by</param>
        public void Increament(double increment)
        {
            lock (_lock) {
                Current += increment;
                OnProgressChanged((int)Math.Round(Current));
            }
        }
    }
}
