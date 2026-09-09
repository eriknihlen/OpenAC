using System.Globalization;
using AcDream.App.UI.Layout;

namespace AcDream.App.UI;

public sealed class RetailSkillTrainingConfirmationController
{
    public const string MessageFormat =
        "Are you sure you want to spend {0} credits to train {1}?";

    private readonly RetailDialogFactory _dialogs;

    public RetailSkillTrainingConfirmationController(RetailDialogFactory dialogs)
        => _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

    public uint Request(
        CharacterStatController.RaiseRequest request,
        CharacterSheet sheet,
        Action<CharacterStatController.RaiseRequest> accepted,
        Action completed)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(completed);

        if (request.Kind != CharacterStatController.RaiseTargetKind.TrainSkill)
            throw new ArgumentException("Only TrainSkill requests require this confirmation.", nameof(request));
        if (request.Cost <= 0 || request.Cost > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(request), "Training cost must fit retail's uint32 field.");

        CharacterSkill skill = sheet.Skills.FirstOrDefault(candidate => candidate.Id == request.StatId)
            ?? throw new InvalidOperationException($"Skill {request.StatId} is absent from the character sheet.");

        string message = string.Format(
            CultureInfo.InvariantCulture,
            MessageFormat,
            request.Cost,
            skill.Name);

        RetailDialogData data = RetailDialogData.Confirmation(message)
            .Set(RetailDialogProperty.TrainSkillId, request.StatId)
            .Set(RetailDialogProperty.TrainSkillCredits, checked((uint)request.Cost));

        return _dialogs.MakeDialog(data, result =>
        {
            if (result.GetBoolean(RetailDialogProperty.ConfirmationResult))
            {
                uint skillId = result.GetUInt32(RetailDialogProperty.TrainSkillId);
                uint credits = result.GetUInt32(RetailDialogProperty.TrainSkillCredits);
                if (skillId != 0u && credits != 0u)
                {
                    accepted(new CharacterStatController.RaiseRequest(
                        CharacterStatController.RaiseTargetKind.TrainSkill,
                        skillId,
                        credits,
                        Amount: 1));
                }
            }

            completed();
        });
    }
}
