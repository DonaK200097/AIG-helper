namespace GHelper.AiMode
{
    /// <summary>
    /// Access to power scheme values. Separated from PowrProf so the plan logic can be tested with a fake.
    /// Read methods return null when the setting is missing or the call failed.
    /// </summary>
    public interface IPowerSettings
    {
        Guid? GetActiveScheme();
        bool SetActiveScheme(Guid scheme);

        uint? ReadAC(Guid scheme, Guid subgroup, Guid setting);
        uint? ReadDC(Guid scheme, Guid subgroup, Guid setting);

        bool WriteAC(Guid scheme, Guid subgroup, Guid setting, uint value);
        bool WriteDC(Guid scheme, Guid subgroup, Guid setting, uint value);
    }
}
