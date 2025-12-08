# simulation_logic/location_ids.py
# ============================================================================
# 標準化地點識別碼模組 (Location ID Module)
# ============================================================================
# 解決問題 2：Log 地點命名混亂
#
# 所有 Log 輸出與前後端通訊應統一使用這些標準 ID：
#   Apartment_F1, Apartment_F2, School, Rest, Gym, Super, Subway, Exterior
#
# 使用方式：
#   from .location_ids import LocationID
#   loc = LocationID.from_string("公寓一樓")  # -> LocationID.APARTMENT_F1
#   print(loc)  # -> "Apartment_F1"
# ============================================================================

from enum import Enum
from typing import Optional


class LocationID(Enum):
    """
    標準化地點識別碼枚舉。
    
    禁止在程式碼中硬編碼字串 (如 if loc == "Room 105")。
    應使用 LocationID.APARTMENT_F1 等枚舉值。
    """
    # --- 外部區域 ---
    EXTERIOR = "Exterior"
    
    # --- 公寓 ---
    APARTMENT_F1 = "Apartment_F1"
    APARTMENT_F2 = "Apartment_F2"
    
    # --- 衛星建築 ---
    SCHOOL = "School"
    REST = "Rest"           # 餐廳 / Cafe
    GYM = "Gym"
    SUPER = "Super"         # 超市
    SUBWAY = "Subway"       # 地鐵
    
    # --- 特殊狀態 ---
    UNKNOWN = "Unknown"

    @classmethod
    def from_string(cls, value: str) -> "LocationID":
        """
        從任意字串轉換為標準 LocationID。
        支援中英文混合輸入。
        
        Args:
            value: 任意地點名稱字串
            
        Returns:
            對應的 LocationID 枚舉值
            
        Example:
            >>> LocationID.from_string("公寓一樓")
            LocationID.APARTMENT_F1
            >>> LocationID.from_string("Apartment_F2")
            LocationID.APARTMENT_F2
        """
        if not value:
            return cls.UNKNOWN
        
        normalized = value.strip().lower()
        
        # 公寓二樓 (必須先檢查，因為 "apartment" 會匹配 "apartment_f2")
        if any(k in normalized for k in ["f2", "二樓", "floor2", "apartment_f2"]):
            return cls.APARTMENT_F2
        
        # 公寓一樓
        if any(k in normalized for k in ["apartment", "公寓", "f1", "一樓"]):
            return cls.APARTMENT_F1
        
        if any(k in normalized for k in ["school", "學校", "学校"]):
            return cls.SCHOOL
        
        if any(k in normalized for k in ["rest", "餐廳", "餐厅", "cafe", "咖啡"]):
            return cls.REST
        
        if any(k in normalized for k in ["gym", "健身房"]):
            return cls.GYM
        
        if any(k in normalized for k in ["super", "超市", "商場", "商场", "便利店"]):
            return cls.SUPER
        
        if any(k in normalized for k in ["subway", "地鐵", "地铁", "metro"]):
            return cls.SUBWAY
        
        if any(k in normalized for k in ["exterior", "室外", "戶外", "户外", "park", "公園", "公园"]):
            return cls.EXTERIOR
        
        return cls.UNKNOWN

    @classmethod
    def get_all_valid_ids(cls) -> list:
        """取得所有有效的地點 ID (排除 UNKNOWN)"""
        return [loc for loc in cls if loc != cls.UNKNOWN]

    @classmethod
    def is_valid_location(cls, value: str) -> bool:
        """檢查字串是否為有效地點"""
        return cls.from_string(value) != cls.UNKNOWN

    def __str__(self) -> str:
        """輸出時只返回值 (如 "Apartment_F1" 而非 "LocationID.APARTMENT_F1")"""
        return self.value
    
    def __repr__(self) -> str:
        return f"LocationID.{self.name}"


# ============================================================================
# 地點群組常數
# ============================================================================

# 室內地點
INDOOR_LOCATIONS = {
    LocationID.APARTMENT_F1,
    LocationID.APARTMENT_F2,
    LocationID.SCHOOL,
    LocationID.REST,
    LocationID.GYM,
    LocationID.SUPER,
    LocationID.SUBWAY,
}

# 室外地點
OUTDOOR_LOCATIONS = {
    LocationID.EXTERIOR,
}

# 公寓相關地點
APARTMENT_LOCATIONS = {
    LocationID.APARTMENT_F1,
    LocationID.APARTMENT_F2,
}


def normalize_to_standard_id(raw_name: str) -> str:
    """
    將任意地點名稱轉換為標準 ID 字串。
    
    這是給 Log 輸出和 JSON 序列化使用的便利函數。
    
    Args:
        raw_name: 任意地點名稱
        
    Returns:
        標準 ID 字串 (如 "Apartment_F1")
    """
    return str(LocationID.from_string(raw_name))


def get_portal_destination_id(portal_name: str) -> str:
    """
    根據傳送門名稱取得對應的標準地點 ID。
    
    Args:
        portal_name: 傳送門名稱 (如 "公寓大門_室內")
        
    Returns:
        標準地點 ID (如 "Apartment_F1")
    """
    # 傳送門 -> 地點 ID 映射
    portal_mapping = {
        "公寓大門_室內": LocationID.APARTMENT_F1,
        "公寓側門_室內": LocationID.APARTMENT_F1,
        "公寓一樓_室內": LocationID.APARTMENT_F1,
        "公寓二樓_室內": LocationID.APARTMENT_F2,
        "公寓頂樓_室內": LocationID.APARTMENT_F2,
        "公寓大門_室外": LocationID.EXTERIOR,
        "公寓側門_室外": LocationID.EXTERIOR,
        "公寓頂樓_室外": LocationID.EXTERIOR,
        "健身房_室內": LocationID.GYM,
        "健身房_室外": LocationID.EXTERIOR,
        "學校門口_室內": LocationID.SCHOOL,
        "學校門口_室外": LocationID.EXTERIOR,
        "餐廳_室內": LocationID.REST,
        "餐廳_室外": LocationID.EXTERIOR,
        "超市側門_室內": LocationID.SUPER,
        "超市左門_室內": LocationID.SUPER,
        "超市右門_室內": LocationID.SUPER,
        "超市側門_室外": LocationID.EXTERIOR,
        "超市左門_室外": LocationID.EXTERIOR,
        "超市右門_室外": LocationID.EXTERIOR,
        "地鐵左樓梯_室內": LocationID.SUBWAY,
        "地鐵右樓梯_室內": LocationID.SUBWAY,
        "地鐵左入口_室外": LocationID.EXTERIOR,
        "地鐵右入口_室外": LocationID.EXTERIOR,
        "地鐵上入口_室外": LocationID.EXTERIOR,
        "地鐵下入口_室外": LocationID.EXTERIOR,
    }
    
    loc_id = portal_mapping.get(portal_name, LocationID.UNKNOWN)
    return str(loc_id)
