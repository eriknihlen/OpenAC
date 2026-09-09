namespace AcDream.Launcher.Core.Installation;

internal sealed class BakeProgressProtocol
{
    private BakeProgressProtocolState _state;

    internal BakeStartedEvent? Started { get; private set; }

    internal BakeCompletedEvent? Completed { get; private set; }

    internal BakeErrorEvent? Error { get; private set; }

    internal string? Violation { get; private set; }

    internal bool Observe(BakeProgressEvent progressEvent)
    {
        ArgumentNullException.ThrowIfNull(progressEvent);
        if (Violation is not null)
        {
            return false;
        }

        switch (progressEvent)
        {
            case BakeHumanOutputEvent:
            case UnknownBakeProgressEvent:
            case FutureBakeProgressEvent:
                return true;
            case MalformedBakeProgressEvent malformed:
                Reject($"Malformed bake progress: {malformed.Reason}");
                return false;
            case BakeStartedEvent started:
                if (_state != BakeProgressProtocolState.AwaitingStarted)
                {
                    Reject(_state == BakeProgressProtocolState.Running
                        ? "The bake protocol emitted more than one v1 started event."
                        : "The bake protocol emitted a known event after its terminal event.");
                    return false;
                }

                Started = started;
                _state = BakeProgressProtocolState.Running;
                return true;
            case BakeWorkProgressEvent:
                if (_state != BakeProgressProtocolState.Running)
                {
                    Reject(KnownEventStateViolation("progress"));
                    return false;
                }

                return true;
            case BakeCompletedEvent completed:
                if (_state != BakeProgressProtocolState.Running)
                {
                    Reject(KnownEventStateViolation("completed"));
                    return false;
                }

                Completed = completed;
                _state = BakeProgressProtocolState.Completed;
                return true;
            case BakeErrorEvent error:
                if (_state != BakeProgressProtocolState.Running)
                {
                    Reject(KnownEventStateViolation("error"));
                    return false;
                }

                Error = error;
                _state = BakeProgressProtocolState.Error;
                return true;
            default:
                Reject("The bake protocol emitted an unsupported known event.");
                return false;
        }
    }

    internal void CompleteInput()
    {
        if (Violation is not null)
        {
            return;
        }

        if (_state == BakeProgressProtocolState.AwaitingStarted)
        {
            Reject("The bake protocol did not emit a v1 started event first.");
        }
        else if (_state == BakeProgressProtocolState.Running)
        {
            Reject("The bake protocol ended without exactly one terminal event.");
        }
    }

    private string KnownEventStateViolation(string eventName) => _state switch
    {
        BakeProgressProtocolState.AwaitingStarted =>
            $"The bake protocol emitted v1 {eventName} before v1 started.",
        BakeProgressProtocolState.Running =>
            $"The bake protocol emitted an invalid v1 {eventName} event.",
        _ => "The bake protocol emitted a known event after its terminal event.",
    };

    private void Reject(string message)
    {
        Violation ??= message;
    }

    private enum BakeProgressProtocolState
    {
        AwaitingStarted,
        Running,
        Completed,
        Error,
    }
}
