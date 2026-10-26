#pragma once
#include <string>
#include <vector>
#include <map>
#include <sstream>
#include <iomanip>
#include <cstdint>

// WinPE 환경에서 외부 종속성 없이 컴파일 가능한 간결한 JSON 빌더/파서 유틸리티
namespace SimpleJson {

inline std::string EscapeString(const std::string& str) {
    std::ostringstream ss;
    for (char c : str) {
        switch (c) {
            case '"':  ss << "\\\""; break;
            case '\\': ss << "\\\\"; break;
            case '\b': ss << "\\b";  break;
            case '\f': ss << "\\f";  break;
            case '\n': ss << "\\n";  break;
            case '\r': ss << "\\r";  break;
            case '\t': ss << "\\t";  break;
            default:
                if ('\x00' <= c && c <= '\x1f') {
                    ss << "\\u" << std::hex << std::setw(4) << std::setfill('0') << (int)c;
                } else {
                    ss << c;
                }
        }
    }
    return ss.str();
}

inline std::string UnescapeString(const std::string& str) {
    std::string result;
    result.reserve(str.length());
    for (size_t i = 0; i < str.length(); ++i) {
        if (str[i] == '\\' && i + 1 < str.length()) {
            char next = str[i + 1];
            switch (next) {
                case '"':  result.push_back('"'); i++; break;
                case '\\': result.push_back('\\'); i++; break;
                case '/':  result.push_back('/'); i++; break;
                case 'b':  result.push_back('\b'); i++; break;
                case 'f':  result.push_back('\f'); i++; break;
                case 'n':  result.push_back('\n'); i++; break;
                case 'r':  result.push_back('\r'); i++; break;
                case 't':  result.push_back('\t'); i++; break;
                default:
                    result.push_back(str[i]);
                    break;
            }
        } else {
            result.push_back(str[i]);
        }
    }
    return result;
}

class JsonValue {
public:
    enum Type { Null, Bool, Number, String, Array, Object };

    Type type = Null;
    bool boolVal = false;
    double numVal = 0.0;
    std::string strVal;
    std::vector<JsonValue> arrVal;
    std::map<std::string, JsonValue> objVal;

    JsonValue() : type(Null) {}
    JsonValue(bool b) : type(Bool), boolVal(b) {}
    JsonValue(int n) : type(Number), numVal(n) {}
    JsonValue(int64_t n) : type(Number), numVal((double)n) {}
    JsonValue(uint64_t n) : type(Number), numVal((double)n) {}
    JsonValue(double n) : type(Number), numVal(n) {}
    JsonValue(const char* s) : type(String), strVal(s) {}
    JsonValue(const std::string& s) : type(String), strVal(s) {}

    static JsonValue CreateObject() {
        JsonValue val;
        val.type = Object;
        return val;
    }

    static JsonValue CreateArray() {
        JsonValue val;
        val.type = Array;
        return val;
    }

    void Set(const std::string& key, const JsonValue& val) {
        type = Object;
        objVal[key] = val;
    }

    void Push(const JsonValue& val) {
        type = Array;
        arrVal.push_back(val);
    }

    std::string ToString() const {
        std::ostringstream ss;
        switch (type) {
            case Null:   ss << "null"; break;
            case Bool:   ss << (boolVal ? "true" : "false"); break;
            case Number: 
                if (numVal == (int64_t)numVal) {
                    ss << (int64_t)numVal;
                } else {
                    ss << std::fixed << std::setprecision(2) << numVal;
                }
                break;
            case String: ss << "\"" << EscapeString(strVal) << "\""; break;
            case Array: {
                ss << "[";
                for (size_t i = 0; i < arrVal.size(); ++i) {
                    if (i > 0) ss << ",";
                    ss << arrVal[i].ToString();
                }
                ss << "]";
                break;
            }
            case Object: {
                ss << "{";
                size_t i = 0;
                for (auto const& [k, v] : objVal) {
                    if (i > 0) ss << ",";
                    ss << "\"" << EscapeString(k) << "\":" << v.ToString();
                    i++;
                }
                ss << "}";
                break;
            }
        }
        return ss.str();
    }
};

} // namespace SimpleJson
