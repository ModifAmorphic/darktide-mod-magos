-- Test fixture script for the rar import test.
-- Gives the archive a multi-file structure (base + descriptor + a scripts/ child)
-- so the per-entry extraction exercises nested paths, not just a single file.
return function()
    print("rar fixture mod loaded")
end
