local M = {}

function M.shoot(api)
    if(api:getAmmoCountInMagazine()>1)then
        api:shootOnce(api:isShootingNeedConsumeAmmo())
    end
    api:shootOnce(api:isShootingNeedConsumeAmmo())
end

return M