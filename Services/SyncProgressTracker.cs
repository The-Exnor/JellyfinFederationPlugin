using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Tracks sync progress for real-time UI updates.
    /// </summary>
    public static class SyncProgressTracker
    {
    private static readonly ConcurrentDictionary<string, SyncProgress> _progress = new();

        /// <summary>
        /// Starts tracking progress for a sync operation.
        /// </summary>
   public static void Start(string operationId, int totalItems)
        {
            _progress[operationId] = new SyncProgress
    {
           OperationId = operationId,
              TotalItems = totalItems,
              ProcessedItems = 0,
      Status = "Starting...",
         StartTime = DateTime.UtcNow
            };
      }

   /// <summary>
        /// Updates progress for a sync operation.
        /// </summary>
    public static void Update(string operationId, int processedItems, string status)
     {
        if (_progress.TryGetValue(operationId, out var progress))
    {
                progress.ProcessedItems = processedItems;
             progress.Status = status;
     progress.LastUpdate = DateTime.UtcNow;
            }
        }

        /// <summary>
     /// Updates progress with total count adjustment.
     /// </summary>
        public static void Update(string operationId, int processedItems, int totalItems, string status)
  {
 if (_progress.TryGetValue(operationId, out var progress))
     {
       progress.TotalItems = totalItems;
       progress.ProcessedItems = processedItems;
  progress.Status = status;
    progress.LastUpdate = DateTime.UtcNow;
  }
  }

   /// <summary>
        /// Completes a sync operation.
        /// </summary>
        public static void Complete(string operationId, bool success, string message)
        {
            if (_progress.TryGetValue(operationId, out var progress))
            {
          progress.ProcessedItems = progress.TotalItems;
                progress.Status = message;
         progress.IsComplete = true;
      progress.Success = success;
      progress.EndTime = DateTime.UtcNow;
         }
  }

     /// <summary>
        /// Gets progress for a sync operation.
      /// </summary>
        public static SyncProgress? Get(string operationId)
     {
            return _progress.TryGetValue(operationId, out var progress) ? progress : null;
        }

      /// <summary>
   /// Clears old completed operations (older than 1 hour).
        /// </summary>
      public static void Cleanup()
      {
      var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var kvp in _progress)
            {
          if (kvp.Value.IsComplete && kvp.Value.EndTime < cutoff)
          {
     _progress.TryRemove(kvp.Key, out _);
      }
      }
        }
    }

    /// <summary>
    /// Represents sync operation progress.
    /// </summary>
    public class SyncProgress
    {
      public string OperationId { get; set; } = string.Empty;
        public int TotalItems { get; set; }
        public int ProcessedItems { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool IsComplete { get; set; }
        public bool Success { get; set; }
        public DateTime StartTime { get; set; }
      public DateTime? LastUpdate { get; set; }
        public DateTime? EndTime { get; set; }

        public int Percentage => TotalItems > 0 ? (int)((ProcessedItems / (double)TotalItems) * 100) : 0;
        
   public TimeSpan? ElapsedTime => EndTime.HasValue 
    ? EndTime.Value - StartTime 
          : DateTime.UtcNow - StartTime;
    }
}
