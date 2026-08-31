namespace Baton.Core;

/// <summary>Discriminant for <see cref="BatonEventArgs.Kind"/>.</summary>
public enum BatonTaskEventKind
{
    /// <summary>The child process has started. <see cref="BatonEventArgs.Pid"/> is valid.</summary>
    Started,

    /// <summary>
    /// A chunk of stdout bytes arrived. <see cref="BatonEventArgs.Seq"/> and
    /// <see cref="BatonEventArgs.Data"/> are valid. Only raised when capture-output is enabled.
    /// </summary>
    StdoutChunk,

    /// <summary>
    /// A chunk of stderr bytes arrived. <see cref="BatonEventArgs.Seq"/> and
    /// <see cref="BatonEventArgs.Data"/> are valid. Only raised when capture-output is enabled.
    /// </summary>
    StderrChunk,

    /// <summary>
    /// The child process has exited. <see cref="BatonEventArgs.ExitCode"/> and
    /// <see cref="BatonEventArgs.ExitReason"/> are valid.
    /// </summary>
    Exited,
}
