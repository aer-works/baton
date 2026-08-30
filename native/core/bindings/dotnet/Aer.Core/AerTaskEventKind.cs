namespace Aer.Core;

/// <summary>Discriminant for <see cref="AerEventArgs.Kind"/>.</summary>
public enum AerTaskEventKind
{
    /// <summary>The child process has started. <see cref="AerEventArgs.Pid"/> is valid.</summary>
    Started,

    /// <summary>
    /// A chunk of stdout bytes arrived. <see cref="AerEventArgs.Seq"/> and
    /// <see cref="AerEventArgs.Data"/> are valid. Only raised when capture-output is enabled.
    /// </summary>
    StdoutChunk,

    /// <summary>
    /// A chunk of stderr bytes arrived. <see cref="AerEventArgs.Seq"/> and
    /// <see cref="AerEventArgs.Data"/> are valid. Only raised when capture-output is enabled.
    /// </summary>
    StderrChunk,

    /// <summary>
    /// The child process has exited. <see cref="AerEventArgs.ExitCode"/> and
    /// <see cref="AerEventArgs.ExitReason"/> are valid.
    /// </summary>
    Exited,
}
