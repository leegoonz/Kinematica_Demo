using Unity.SnapshotDebugger;
using UnityEngine;

public partial class ParkourAbility : SnapshotProvider, Ability
{
    public struct SerializedInput
    {
        public bool jumpButton;

        public void WriteToStream(Buffer buffer)
        {
            buffer.Write(jumpButton);
        }

        public void ReadFromStream(Buffer buffer)
        {
            jumpButton = buffer.ReadBoolean();
        }
    }

    public override void OnEarlyUpdate(bool rewind)
    {
        capture.jumpButton = Input.GetButton("A Button");
    }

    public override void WriteToStream(Buffer buffer)
    {
        capture.WriteToStream(buffer);
    }

    public override void ReadFromStream(Buffer buffer)
    {
        capture.ReadFromStream(buffer);
    }
}
