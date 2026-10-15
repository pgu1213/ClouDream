#ifndef CLOUD_CELL_RANDOM_INCLUDED
#define CLOUD_CELL_RANDOM_INCLUDED

// 절대 셀과 시드로 동일한 24비트 난수를 생성합니다.
float CellRandom(int2 cell, uint salt)
{
    uint value = asuint(cell.x) * 747796405u + asuint(cell.y) * 2891336453u;
    value += (uint)_SkyPlacement.z * 277803737u + salt * 1597334677u;
    value = (value ^ (value >> 16)) * 2246822519u;
    value = (value ^ (value >> 13)) * 3266489917u;
    value ^= value >> 16;
    return (value & 0x00ffffffu) / 16777216.0;
}

#endif
